using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using LightContainmentZoneDecontamination;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class DecontaminationActionTool : ActionTool
{
    private static readonly string[] Actions = { "force", "disable", "restore" };

    public DecontaminationActionTool(DirectorContext ctx)
        : base(ctx) { }

    public override string Name => "decontamination";
    public override string Title => "LCZ decontamination";

    public override string Description =>
        "Light Containment Zone decontamination: force = start now, disable = turn off for duration_sec "
        + "(normal timer returns after that, max = config limit), restore = normal timer.";

    public override JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["action"] = Schema.Str("What to do", Actions),
                ["duration_sec"] = Schema.Int("For disable: restore the normal timer after this many seconds", 1, 3600),
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

        return new PreparedAction
        {
            Summary = action == "disable" ? $"disable for {duration} s" : action,
            Apply = () =>
            {
                Decontamination.Status = action switch
                {
                    "force" => DecontaminationController.DecontaminationStatus.Forced,
                    "disable" => DecontaminationController.DecontaminationStatus.Disabled,
                    _ => DecontaminationController.DecontaminationStatus.None,
                };
                if (action != "disable")
                    return;
                Ctx.Schedule(duration, "вернуть обеззараживание", () =>
                {
                    if (Decontamination.Status == DecontaminationController.DecontaminationStatus.Disabled)
                        Decontamination.Status = DecontaminationController.DecontaminationStatus.None;
                    Ctx.Log("decontamination: обычный таймер возвращён");
                });
            },
        };
    }
}
