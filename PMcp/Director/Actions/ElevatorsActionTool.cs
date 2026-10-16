using System;
using System.Collections.Generic;
using System.Linq;
using Interactables.Interobjects;
using LabApi.Features.Wrappers;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class ElevatorsActionTool : ActionTool
{
    private static readonly string[] Actions = { "lock", "unlock" };
    private static readonly string[] Groups = new[] { "all" }.Concat(Enum.GetNames(typeof(ElevatorGroup))).ToArray();

    public ElevatorsActionTool(DirectorContext ctx)
        : base(ctx) { }

    public override string Name => "elevators";
    public override string Title => "Elevators";

    public override string Description => "Lock or unlock elevators (a group or all). lock is released automatically after duration_sec.";

    public override JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["action"] = Schema.Str("What to do", Actions),
                ["group"] = Schema.Str("Elevator group", Groups),
                ["duration_sec"] = Schema.Int("Unlock after this many seconds", 1, 3600),
            },
            "action"
        );

    public override PreparedAction Prepare(Dictionary<string, object?> args)
    {
        string action = OneOf(args, "action", Actions);
        string groupText = ToolArgs.String(args, "group")?.Trim() ?? "all";
        ElevatorGroup? group = null;
        if (!string.Equals(groupText, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse(groupText, true, out ElevatorGroup parsed))
                throw new ArgumentException($"No elevator group '{groupText}'. Allowed: {string.Join(", ", Groups)}");
            group = parsed;
        }
        int max = Ctx.Config.MaxLockSeconds;
        int duration = ToolArgs.Int(args, "duration_sec", max, 1, 3600);
        if (duration > max)
            throw new ArgumentException($"duration_sec max is {max}");

        return new PreparedAction
        {
            Summary = action == "lock" ? $"lock {groupText} for {duration} s" : $"unlock {groupText}",
            Apply = () =>
            {
                List<Elevator> elevators = (group == null ? Elevator.List : Elevator.GetByGroup(group.Value)).Where(e => e != null).ToList();
                if (elevators.Count == 0)
                    throw new ArgumentException($"No elevators of group {groupText} on this map");

                foreach (Elevator elevator in elevators)
                {
                    SetLock(elevator, action == "lock");
                }

                if (action == "lock")
                {
                    Ctx.Schedule(
                        duration,
                        $"разблокировка лифтов {groupText}",
                        () =>
                        {
                            foreach (Elevator elevator in elevators)
                                SetLock(elevator, false);
                            Ctx.Log($"elevators: разблокированы {groupText}");
                        }
                    );
                }
            },
        };
    }

    private static void SetLock(Elevator elevator, bool locked)
    {
        foreach (Door door in elevator.Doors)
        {
            if (door != null && !door.IsDestroyed)
                door.Lock(DirectorLock, locked);
        }
    }
}