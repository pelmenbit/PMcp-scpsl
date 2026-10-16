using System;
using System.Collections.Generic;
using System.Linq;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class ReplanTail
{
    public bool Keep { get; set; }
    public string Summary { get; set; } = "";
    public List<TimelineEntry> Timeline { get; } = new();
}

public sealed class ReplanTailTool : ITool
{
    private const int LeadSeconds = 10;

    private readonly DirectorContext _ctx;
    private readonly Dictionary<string, ActionTool> _actions = new(StringComparer.OrdinalIgnoreCase);

    public ReplanTailTool(DirectorContext ctx, IEnumerable<ActionTool> actions)
    {
        _ctx = ctx;
        foreach (ActionTool action in actions)
            _actions[action.Name] = action;

        Description =
            "Replace ALL not-yet-played events of the round plan with a new list, or keep_current=true to leave them as they are. "
            + $"at_sec = round second, at least {LeadSeconds} s after the current round second. Same event format as in the plan: "
            + "{at_sec, action, args, when?, note}. " + Conditions.Help + "\nActions:\n"
            + string.Join("\n", _actions.Values.Select(PlanRoundTool.Signature));
    }

    public string Name => "replan_tail";
    public string Title => "Replan the rest of the round";
    public string Description { get; }

    public JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["keep_current"] = Schema.Bool("true = the remaining plan still fits, change nothing", false),
                ["summary"] = Schema.Str("What changes and why, 1 sentence (Russian)"),
                ["timeline"] = PlanRoundTool.TimelineSchema("New remaining events, replace all upcoming ones"),
            }
        );

    public object Execute(Dictionary<string, object?> args)
    {
        var tail = new ReplanTail
        {
            Keep = ToolArgs.Bool(args, "keep_current", false),
            Summary = TextSanitizer.Clean(ToolArgs.String(args, "summary"), 300) ?? "",
        };

        if (tail.Keep)
        {
            _ctx.TailAccepted?.Invoke(tail);
            return new JsonObject { ["accepted"] = true, ["note"] = "Remaining plan kept. Do not call again." };
        }

        if (!args.TryGetValue("timeline", out object? t) || t is not List<object?> timeline || timeline.Count == 0)
            throw new ArgumentException("timeline is required (or keep_current=true)");

        var dropped = new List<string>();
        var fixedNotes = new List<string>();
        int minSec = (int)_ctx.RoundSec + LeadSeconds;
        tail.Timeline.AddRange(PlanRoundTool.ParseTimeline(timeline, minSec, _ctx.Config.MaxTimelineEntries, _ctx, _actions, dropped, fixedNotes));

        if (tail.Timeline.Count == 0 || tail.Timeline.Count * 2 < timeline.Count)
            throw new ArgumentException(
                $"Rejected: only {tail.Timeline.Count} of {timeline.Count} events are valid.\n- " + string.Join("\n- ", dropped)
            );

        _ctx.TailAccepted?.Invoke(tail);
        _ctx.Log(
            $"хвост плана заменён: событий {tail.Timeline.Count} из {timeline.Count}"
                + (fixedNotes.Count > 0 ? "\n  исправлено: " + string.Join("\n  исправлено: ", fixedNotes) : "")
                + (dropped.Count > 0 ? "\n  отброшено: " + string.Join("\n  отброшено: ", dropped) : "")
        );
        return new JsonObject
        {
            ["accepted"] = true,
            ["timeline"] = tail.Timeline.Count,
            ["fixed"] = fixedNotes,
            ["dropped"] = dropped,
            ["note"] = "Accepted. Do not call again.",
        };
    }
}
