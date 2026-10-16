using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class WarheadActionTool : ActionTool
{
    private static readonly string[] Actions = { "start", "stop", "lock", "unlock", "lever_on", "lever_off" };

    public WarheadActionTool(DirectorContext ctx)
        : base(ctx) { }

    public override string Name => "warhead";
    public override string Title => "Alpha Warhead";

    public override string Description =>
        "Warhead: start (not before the configured round second), stop, lock/unlock (forbid cancelling), "
        + "lever_on/lever_off (surface lever). lock and lever_off return to normal after duration_sec (max = config limit). "
        + "Detonation almost always ends the round - use only as a climax.";

    public override JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["action"] = Schema.Str("What to do", Actions),
                ["duration_sec"] = Schema.Int("For lock and lever_off: revert after this many seconds", 1, 3600),
            },
            "action"
        );

    public override PreparedAction Prepare(Dictionary<string, object?> args)
    {
        string action = OneOf(args, "action", Actions);
        int max = Ctx.Config.MaxLockSeconds;
        int duration = ToolArgs.Int(args, "duration_sec", max, 1, 3600);
        if (duration > max)
            throw new ArgumentException($"duration_sec max is {max}");
        bool timed = action == "lock" || action == "lever_off";

        return new PreparedAction
        {
            Summary = timed ? $"{action} for {duration} s" : action,
            Apply = () =>
            {
                if (!Warhead.Exists)
                    throw new ArgumentException("No warhead on this map");
                switch (action)
                {
                    case "start":
                        if (Warhead.IsDetonated || Warhead.IsDetonationInProgress)
                            throw new ArgumentException("Warhead is already running or detonated");
                        double roundSec = Round.Duration.TotalSeconds;
                        if (roundSec < Ctx.Config.WarheadMinRoundSeconds)
                            throw new ArgumentException($"Too early: round is at {roundSec:0} s, start allowed from {Ctx.Config.WarheadMinRoundSeconds} s");
                        Warhead.Start();
                        break;
                    case "stop":
                        Warhead.Stop();
                        break;
                    case "lock":
                        Warhead.IsLocked = true;
                        Ctx.Schedule(duration, "снять блокировку боеголовки", () =>
                        {
                            Warhead.IsLocked = false;
                            Ctx.Log("warhead: блокировка снята");
                        });
                        break;
                    case "unlock":
                        Warhead.IsLocked = false;
                        break;
                    case "lever_on":
                        Warhead.LeverStatus = true;
                        break;
                    case "lever_off":
                        Warhead.LeverStatus = false;
                        Ctx.Schedule(duration, "вернуть рычаг боеголовки", () =>
                        {
                            Warhead.LeverStatus = true;
                            Ctx.Log("warhead: рычаг возвращён");
                        });
                        break;
                }
            },
        };
    }
}
