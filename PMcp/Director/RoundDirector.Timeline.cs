using System;
using System.Linq;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using MEC;
using PMcp.Ai;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed partial class RoundDirector
{
    private void StartTimeline()
    {
        KillTimeline();
        _timelineStarted = true;
        if (_plan == null)
            return;

        double elapsed = Round.Duration.TotalSeconds;
        int skipped = 0;
        foreach (TimelineEntry entry in _plan.Timeline)
        {
            double delay = entry.AtSec - elapsed;
            if (delay < -5)
            {
                skipped++;
                continue;
            }
            Schedule(entry, delay);
        }
        _ctx.Log($"таймлайн запущен: событий {_plan.Timeline.Count - skipped}{(skipped > 0 ? $", пропущено прошедших {skipped}" : "")}");
    }

    private void Schedule(TimelineEntry entry, double delay) =>
        _timeline.Add(Timing.CallDelayed((float)Math.Max(0, delay), () => RunEntry(entry)));

    private void RunEntry(TimelineEntry entry)
    {
        if (!D.Enabled || !Round.IsRoundStarted || Round.IsRoundEnded)
            return;

        string reason;
        bool ok;
        if (_paused)
        {
            ok = false;
            reason = "дирижёр на паузе";
        }
        else if (_skipped.Remove(entry))
        {
            ok = false;
            reason = "пропущено админом";
        }
        else
        {
            try
            {
                ok = Conditions.Check(entry.When, out reason);
            }
            catch (ArgumentException e)
            {
                ok = false;
                reason = e.Message;
            }
        }

        _ctx.ActionSource = "timeline";
        try
        {
            if (!ok)
            {
                _ctx.Log($"событие {entry.AtSec} с ({entry.Action}) пропущено: {reason}");
                _ctx.RecordAction(entry.Action, "не выполнено: " + reason, "skipped");
                return;
            }
            ToolCallResult result = _actor.ExecuteInline(entry.Action, entry.Args, "timeline");
            if (result.IsError)
                Logger.Warn($"Дирижёр: событие {entry.AtSec} с ({entry.Action}) не выполнено: {result.Text}");
        }
        finally
        {
            _ctx.ActionSource = "ai";
        }
    }

    private void KillTimeline()
    {
        foreach (CoroutineHandle handle in _timeline)
        {
            if (handle.IsRunning)
                Timing.KillCoroutines(handle);
        }
        _timeline.Clear();
    }

    private TimelineEntry? NextEntry()
    {
        if (_plan == null)
            return null;
        long now = _ctx.RoundSec;
        return _plan.Timeline.FirstOrDefault(e => e.AtSec >= now && !_skipped.Contains(e));
    }

    private void MaybeTail()
    {
        if (_tailDone || _tailRun != null || D.TailReplanAtSeconds <= 0 || _plan == null || !_timelineStarted)
            return;
        if (_ctx.RoundSec < D.TailReplanAtSeconds || Busy)
            return;
        _tailDone = true;
        if (!StartTail(null, out string message))
            Logger.Warn("Дирижёр: хвост плана не переписан - " + message);
    }

    private bool StartTail(Action<string, bool>? notify, out string message)
    {
        long now = _ctx.RoundSec;
        var task = new JsonObject
        {
            ["round_sec"] = now,
            ["plan_theme"] = _plan!.Theme,
            ["plan_summary"] = _plan.Summary,
            ["done_actions"] = _ctx.RecentActions.Select(ActionJson).ToList(),
            ["remaining_plan"] = _plan.Timeline.Where(e => e.AtSec > now).Select(EntryJson).ToList(),
            ["state"] = StateJson(),
        };

        int token = _roundToken;
        AgentRun run = NewRun();
        bool started = _agent.TryStart(
            "Replan the rest of the round. Situation now:\n" + Json.Write(task),
            DirectorPrompts.Tail(D, _library),
            new AgentRunOptions
            {
                Runner = _tailRunner,
                MaxToolRounds = D.PlanMaxToolRounds,
                MaxOutputTokens = D.MaxOutputTokens,
                RequiredTool = "replan_tail",
                Model = PlannerModel,
                Run = run,
            },
            result =>
            {
                RecordRun(result, "хвост плана");
                if (_tailRun != run)
                    return;
                _tailRun = null;
                if (token != _roundToken)
                    return;
                string text = TextSanitizer.StripTags(result.Text);
                _ctx.Log($"хвост плана ({Stats(result)}): {text}");
                notify?.Invoke($"Хвост плана ({Stats(result)}): {text}", result.Success);
            },
            out string error
        );
        if (!started)
        {
            message = error;
            return false;
        }
        _tailRun = run;
        message = $"Переписываю хвост плана ({PlannerModel}), раунд на {now} с";
        _ctx.Log(message);
        return true;
    }

    private void OnTailAccepted(ReplanTail tail)
    {
        if (tail.Keep || _plan == null)
        {
            _ctx.Log("хвост плана оставлен как есть" + (tail.Summary.Length > 0 ? ": " + tail.Summary : ""));
            return;
        }

        long now = _ctx.RoundSec;
        KillTimeline();
        _plan.Timeline.RemoveAll(e => e.AtSec > now);
        _plan.Timeline.AddRange(tail.Timeline);
        _plan.Timeline.Sort((a, b) => a.AtSec.CompareTo(b.AtSec));
        _skipped.Clear();
        double elapsed = Round.Duration.TotalSeconds;
        foreach (TimelineEntry entry in tail.Timeline)
            Schedule(entry, entry.AtSec - elapsed);
        _ctx.Log(
            $"новый хвост плана ({tail.Timeline.Count} событий){(tail.Summary.Length > 0 ? ": " + tail.Summary : "")}\n"
                + string.Join("\n", tail.Timeline.Select(e => e.Describe()))
        );
    }
}
