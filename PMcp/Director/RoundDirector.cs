using System;
using System.Collections.Generic;
using System.IO;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles;
using PMcp.Ai;
using PMcp.Events;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed partial class RoundDirector
{
    private static readonly List<string> ReactionEvents =
        new()
        {
            "death",
            "escaped",
            "wave_respawned",
            "generator_activated",
            "decontamination_started",
            "warhead_started",
            "warhead_stopped",
            "warhead_detonated",
            "scp079_recontained",
            "cuffed",
        };

    private const int RunLogLimit = 10;

    private readonly Config _config;
    private readonly AiAgent _agent;
    private readonly ToolRunner _planner;
    private readonly ToolRunner _actor;
    private readonly ToolRunner _tailRunner;
    private readonly EventLog _events;
    private readonly DirectorContext _ctx;
    private readonly CassieLibrary _library;
    private readonly string _statePath;
    private readonly string _metricsPath;
    private readonly List<CoroutineHandle> _timeline = new();
    private readonly Dictionary<int, RoleTypeId> _assigned = new();
    private readonly HashSet<TimelineEntry> _skipped = new();
    private readonly List<string> _runLog = new();
    private readonly List<string> _themes = new();
    private readonly List<HashSet<string>> _scpHistory = new();

    private CoroutineHandle _loop;
    private RoundPlan? _plan;
    private RoundPlan? _nextPlan;
    private PendingPlan? _pending;
    private PlanJob? _job;
    private AgentRun? _tailRun;
    private AgentRun? _reactRun;
    private int _roundToken;
    private bool _autoPlanBlocked;
    private bool _autoPrepared;
    private bool _paused;
    private bool _tailDone;
    private bool _timelineStarted;
    private bool _libraryChecked;
    private float _nextPlanAttemptAt;
    private float _nextReactionAt;
    private int _reactions;
    private long _lastSeq;
    private Dictionary<string, int>? _aliveAtWindow;
    private long _roundTokens;
    private int _roundRuns;

    public RoundDirector(
        Config config,
        AiAgent agent,
        ToolRunner planner,
        ToolRunner actor,
        ToolRunner tailRunner,
        EventLog events,
        DirectorContext ctx,
        CassieLibrary library,
        string configDirectory
    )
    {
        _config = config;
        _agent = agent;
        _planner = planner;
        _actor = actor;
        _tailRunner = tailRunner;
        _events = events;
        _ctx = ctx;
        _library = library;
        _statePath = Path.Combine(configDirectory, "director_state.json");
        _metricsPath = Path.Combine(configDirectory, "director_plans.jsonl");
        _ctx.PlanAccepted = OnPlanAccepted;
        _ctx.TailAccepted = OnTailAccepted;
    }

    private DirectorConfig D => _config.Director;

    private string PlannerModel => string.IsNullOrWhiteSpace(D.PlannerModel) ? _config.Ai.Model : D.PlannerModel.Trim();
    private string ReactorModel => string.IsNullOrWhiteSpace(D.ReactorModel) ? _config.Ai.Model : D.ReactorModel.Trim();

    private bool Busy => _agent.Running >= Math.Max(1, _config.Ai.MaxConcurrentRuns);

    public void Start()
    {
        ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
        ServerEvents.RoundStarted += OnRoundStarted;
        ServerEvents.RoundEnded += OnRoundEnded;
        PlayerEvents.ChangingRole += OnChangingRole;
        _lastSeq = _events.LatestSeq;
        LoadState();
        _loop = Timing.RunCoroutine(Loop());
    }

    public void Stop()
    {
        ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
        ServerEvents.RoundStarted -= OnRoundStarted;
        ServerEvents.RoundEnded -= OnRoundEnded;
        PlayerEvents.ChangingRole -= OnChangingRole;
        if (_loop.IsRunning)
            Timing.KillCoroutines(_loop);
        KillTimeline();
        CancelRuns();
    }

    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Logger.Error($"Дирижёр: {what} упал: {e}");
        }
    }

    private void OnWaitingForPlayers() => Guard(nameof(OnWaitingForPlayers), () =>
    {
        int previousToken = _roundToken;
        _roundToken++;
        _plan = _nextPlan;
        _nextPlan = null;
        CarryPending(previousToken);
        if (_job != null && _job.Kind == PlanKind.Lobby)
        {
            _job.Run.Cancel();
            _job = null;
            _ctx.PlanningAhead = false;
        }
        _tailRun?.Cancel();
        _tailRun = null;
        _reactRun?.Cancel();
        _reactRun = null;
        _autoPlanBlocked = false;
        _autoPrepared = false;
        _paused = false;
        _tailDone = false;
        _timelineStarted = false;
        _reactions = 0;
        _roundTokens = 0;
        _roundRuns = 0;
        _runLog.Clear();
        _assigned.Clear();
        _skipped.Clear();
        _nextPlanAttemptAt = 0;
        _lastSeq = _events.LatestSeq;
        _aliveAtWindow = null;
        KillTimeline();
        if (_plan != null)
            _ctx.Log($"план, написанный заранее, на этот раунд: «{_plan.Theme}», ролей {_plan.Roles.Count}, событий {_plan.Timeline.Count}");
        CheckLibrary();
    });

    private void OnRoundStarted() => Guard(nameof(OnRoundStarted), () =>
    {
        _lastSeq = _events.LatestSeq;
        _aliveAtWindow = null;
        _nextReactionAt = _ctx.Now + D.ReactionIntervalSeconds;
        int token = _roundToken;
        Timing.CallDelayed(3f, () => Guard(nameof(RecordScps), () => RecordScps(token)));
        if (!D.Enabled || _plan == null)
            return;
        RememberTheme(_plan.Theme);
        StartTimeline();
    });

    private void OnRoundEnded(RoundEndedEventArgs ev) => Guard(nameof(OnRoundEnded), () =>
    {
        KillTimeline();
        if (!D.Enabled)
            return;
        if (D.RoundEndSummary)
            RoundEndReport(ev.LeadingTeam.ToString());
        if (D.AutoPrepareOnRoundEnd && _nextPlan == null && _job == null && !_autoPrepared)
        {
            _autoPrepared = true;
            if (!StartPlanning(null, PlanKind.Ahead, 0, out string message))
                Logger.Warn("Дирижёр: план на следующий раунд не начат - " + message);
        }
    });

    private IEnumerator<float> Loop()
    {
        while (true)
        {
            yield return Timing.WaitForSeconds(1f);
            try
            {
                if (!D.Enabled)
                    continue;
                if (!Round.IsRoundStarted)
                    MaybePlan();
                else if (!Round.IsRoundEnded)
                {
                    MaybeAutoPrepare();
                    if (_paused)
                        continue;
                    MaybeReact();
                    MaybeTail();
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Дирижёр: цикл упал: {e}");
            }
        }
    }

    private AgentRun NewRun()
    {
        var run = new AgentRun();
        if (D.MaxRunSeconds > 0)
            Timing.CallDelayed(D.MaxRunSeconds, run.Cancel);
        return run;
    }

    private void CancelRuns()
    {
        _job?.Run.Cancel();
        _tailRun?.Cancel();
        _reactRun?.Cancel();
    }

    private void RecordRun(AgentResult result, string what)
    {
        _roundTokens += _agent.Ledger.Cost(result);
        _roundRuns++;
        _runLog.Add($"{_ctx.RoundSec,5} с {what}: {Stats(result)}{(result.Cancelled ? " - отменён" : result.Success ? "" : " - ошибка")}");
        if (_runLog.Count > RunLogLimit)
            _runLog.RemoveAt(0);
    }

    private void Fail(string what, AgentResult result, Action<string, bool>? notify)
    {
        string why = TextSanitizer.StripTags(result.Text);
        Logger.Warn($"Дирижёр: {what} ({Stats(result)}): {why}");
        notify?.Invoke($"{what} ({Stats(result)}): {(why.Length > 400 ? why.Substring(0, 400) + "..." : why)}", false);
    }

    private static string Stats(AgentResult r) =>
        $"раундов {r.Rounds}, вызовов {r.ToolCalls}, токены {r.PromptTokens}+{r.CompletionTokens}"
        + (r.CachedTokens > 0 ? $" (из кэша {r.CachedTokens})" : "")
        + $", {r.Seconds:0.0} с";
}
