using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Console;
using PMcp.Ai;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed partial class RoundDirector
{
    private void MaybeReact()
    {
        if (_reactRun != null || _reactions >= D.MaxReactionsPerRound || _ctx.Now < _nextReactionAt)
            return;

        Dictionary<string, int> aliveNow = AliveByTeam();
        if (_aliveAtWindow == null)
            _aliveAtWindow = new Dictionary<string, int>(aliveNow);
        foreach (KeyValuePair<string, int> pair in aliveNow)
        {
            if (!_aliveAtWindow.TryGetValue(pair.Key, out int seen) || pair.Value > seen)
                _aliveAtWindow[pair.Key] = pair.Value;
        }

        if (_events.LatestSeq == _lastSeq || Busy)
            return;
        List<JsonObject> fresh = _events.Snapshot(_lastSeq, 200, ReactionEvents, out _);
        ReactionDigest digest = ReactionDigest.Build(fresh, _aliveAtWindow, aliveNow, D, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (digest.Triggers.Count == 0)
            return;

        if (!StartReaction(digest.Triggers, digest.Summary, null, out string error))
        {
            _nextReactionAt = _ctx.Now + 10f;
            Logger.Warn("Дирижёр: реакция не начата - " + error);
            return;
        }
        _reactions++;
        _lastSeq = _events.LatestSeq;
        _aliveAtWindow = aliveNow;
        _nextReactionAt = _ctx.Now + D.ReactionIntervalSeconds;
    }

    private bool StartReaction(List<string> triggers, JsonObject summary, Action<string, bool>? notify, out string message)
    {
        long now = _ctx.RoundSec;
        var task = new JsonObject
        {
            ["round_sec"] = now,
            ["plan_theme"] = _plan?.Theme ?? "(no plan)",
            ["summary"] = summary,
            ["recent_director_actions"] = _ctx.RecentActions.Skip(Math.Max(0, _ctx.RecentActions.Count - 5)).Select(ActionJson).ToList(),
            ["upcoming_plan"] = _plan == null
                ? new List<object>()
                : _plan.Timeline.Where(e => e.AtSec > now && e.AtSec <= now + 180 && !_skipped.Contains(e)).Take(3).Select(EntryJson).ToList(),
        };

        int token = _roundToken;
        AgentRun run = NewRun();
        bool started = _agent.TryStart(
            "What happened since your last reaction (summary, noise already filtered out):\n" + Json.Write(task),
            DirectorPrompts.Reactor(D, _library),
            new AgentRunOptions
            {
                Runner = _actor,
                MaxToolRounds = D.ReactionMaxToolRounds,
                MaxOutputTokens = D.MaxOutputTokens,
                Model = ReactorModel,
                Run = run,
            },
            result =>
            {
                RecordRun(result, "реакция");
                if (_reactRun != run)
                    return;
                _reactRun = null;
                if (token != _roundToken)
                    return;
                string text = TextSanitizer.StripTags(result.Text);
                bool skipped = result.ToolCalls == 0
                    && (text.StartsWith("skip", StringComparison.OrdinalIgnoreCase) || text.StartsWith("пропуск", StringComparison.OrdinalIgnoreCase));
                if (!skipped || notify != null)
                    _ctx.Log($"реакция ({Stats(result)}): {text}");
                notify?.Invoke($"Реакция ({Stats(result)}): {(skipped ? "модель решила пропустить" : text)}", result.Success);
            },
            out message
        );
        if (!started)
            return false;

        _ctx.Log("реакция на: " + string.Join("; ", triggers));
        _reactRun = run;
        return true;
    }

    private void RoundEndReport(string leadingTeam)
    {
        int escaped = _events.RoundEscapes;
        int deaths = _events.RoundDeaths;
        List<string> scps = PlayerInfo.RealPlayers().Where(p => p.IsAlive && p.IsSCP).Select(p => p.Role.ToString().Replace("Scp", "SCP-")).ToList();
        string winner = leadingTeam switch
        {
            "FacilityForces" => "Фонд",
            "ChaosInsurgency" => "Повстанцы Хаоса",
            "Anomalies" => "SCP-объекты",
            _ => "ничья",
        };

        _ctx.LastCassieAt = -1000f;
        _ctx.ActionSource = "round_end";
        try
        {
            if (_library.Get("round_end") != null)
                Report(_actor.ExecuteInline(
                    "cassie",
                    new Dictionary<string, object?>
                    {
                        ["template"] = "round_end",
                        ["vars"] = new Dictionary<string, object?> { ["escaped"] = (double)Math.Min(escaped, 999), ["scps"] = (double)scps.Count },
                    },
                    "round_end"
                ));

            string text = $"<b>Итог смены</b>\nПобеда: <b>{winner}</b> | сбежало: {escaped} | погибло: {deaths}"
                + (scps.Count > 0 ? $"\nSCP на свободе: {string.Join(", ", scps)}" : "");
            Report(_actor.ExecuteInline("broadcast", new Dictionary<string, object?> { ["text"] = text, ["duration"] = 15.0 }, "round_end"));
        }
        finally
        {
            _ctx.ActionSource = "ai";
        }

        static void Report(ToolCallResult result)
        {
            if (result.IsError)
                Logger.Warn("Дирижёр: итог раунда: " + result.Text);
        }
    }
}
