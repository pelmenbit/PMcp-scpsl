using System;
using System.Collections.Generic;
using System.Linq;
using Interactables.Interobjects.DoorUtils;
using LabApi.Features.Wrappers;
using MapGeneration;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class DoorsActionTool : ActionTool
{
    private static readonly string[] Actions = { "open", "close", "lock", "unlock", "lockdown" };

    public DoorsActionTool(DirectorContext ctx)
        : base(ctx) { }

    public override string Name => "doors";
    public override string Title => "Doors";

    public override string Description =>
        "Doors of a zone or one named door (names: facility_info). action: open, close, lock, unlock, "
        + "lockdown (close and lock). lock and lockdown are released automatically after duration_sec "
        + "(default and max = config limit). Elevator doors are never touched.";

    public override JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["action"] = Schema.Str("What to do", Actions),
                ["zone"] = Schema.Str("Zone, if name is not set", ZoneNames),
                ["name"] = Schema.Str("Door name (NameTag), e.g. GATE_A, CHECKPOINT_LCZ_A"),
                ["duration_sec"] = Schema.Int("Release the lock after this many seconds", 1, 3600),
            },
            "action"
        );

    public override PreparedAction Prepare(Dictionary<string, object?> args)
    {
        string action = OneOf(args, "action", Actions);
        string? name = ToolArgs.String(args, "name")?.Trim();
        FacilityZone? zone = string.IsNullOrEmpty(name) ? ParseZone(ToolArgs.String(args, "zone")) : null;
        int max = Ctx.Config.MaxLockSeconds;
        int duration = ToolArgs.Int(args, "duration_sec", max, 1, 3600);
        if (duration > max)
            throw new ArgumentException($"duration_sec max is {max}");

        bool locking = action == "lock" || action == "lockdown";
        string scope = string.IsNullOrEmpty(name) ? ZoneText(zone) : name!;
        return new PreparedAction
        {
            Summary = locking ? $"{action} {scope} for {duration} s" : $"{action} {scope}",
            Apply = () =>
            {
                List<Door> doors = Collect(name, zone);
                foreach (Door door in doors)
                {
                    switch (action)
                    {
                        case "open":
                            door.IsOpened = true;
                            break;
                        case "close":
                            door.IsOpened = false;
                            break;
                        case "lock":
                            door.Lock(DirectorLock, true);
                            break;
                        case "unlock":
                            door.Lock(DirectorLock, false);
                            break;
                        case "lockdown":
                            door.IsOpened = false;
                            door.Lock(DirectorLock, true);
                            break;
                    }
                }

                if (locking)
                {
                    Ctx.Schedule(
                        duration,
                        $"разблокировка {scope}",
                        () =>
                        {
                            foreach (Door door in doors)
                            {
                                if (!door.IsDestroyed)
                                    door.Lock(DirectorLock, false);
                            }
                            Ctx.Log($"doors: снята блокировка {scope}");
                        }
                    );
                }
            },
        };
    }

    private static List<Door> Collect(string? name, FacilityZone? zone)
    {
        if (!string.IsNullOrEmpty(name))
        {
            Door? door = Door.Get(name!);
            if (door == null)
                throw new ArgumentException($"No door named '{name}' - see facility_info for names");
            return new List<Door> { door };
        }

        IEnumerable<Door> source = zone == null ? Door.List : Door.Get(zone.Value);
        return source.Where(d => d != null && !d.IsDestroyed && d is not LabApi.Features.Wrappers.ElevatorDoor).ToList();
    }
}
