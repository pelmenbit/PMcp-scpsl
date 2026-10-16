using System;
using System.Collections.Generic;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class TeslasActionTool : ActionTool
{
    private static readonly string[] Actions = { "disable", "enable" };

    public TeslasActionTool(DirectorContext ctx)
        : base(ctx) { }

    public override string Name => "teslas";
    public override string Title => "Tesla gates";

    public override string Description => "HCZ tesla gates: disable for duration_sec (re-enable automatically), enable = turn on now.";

    public override JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["action"] = Schema.Str("What to do", Actions),
                ["duration_sec"] = Schema.Int("Disable for this many seconds", 1, 3600, 60),
            },
            "action"
        );

    public override PreparedAction Prepare(Dictionary<string, object?> args)
    {
        string action = OneOf(args, "action", Actions);
        int max = Ctx.Config.MaxLockSeconds;
        int duration = ToolArgs.Int(args, "duration_sec", 60, 1, 3600);
        if (duration > max)
            throw new ArgumentException($"duration_sec max is {max}");

        return new PreparedAction
        {
            Summary = action == "disable" ? $"disabled for {duration} s" : "enabled",
            Apply = () => Ctx.TeslasDisabledUntil = action == "disable" ? Ctx.Now + duration : 0f,
        };
    }
}
