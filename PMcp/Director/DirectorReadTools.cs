using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class FacilityInfoTool : ITool
{
    private readonly DirectorContext? _ctx;

    public FacilityInfoTool(DirectorContext? ctx)
    {
        _ctx = ctx;
    }

    public string Name => "facility_info";
    public string Title => "Facility map";

    public string Description =>
        "What is on the map right now: named doors per zone (open/locked), elevator groups, "
        + "tesla gate count, warhead, decontamination, MTF and Chaos wave timers. No arguments.";

    public JsonObject InputSchema { get; } = Schema.Object(new JsonObject());

    public object Execute(Dictionary<string, object?> args)
    {
        var doors = new JsonObject();
        foreach (Door door in Door.List)
        {
            if (door == null || door.IsDestroyed || string.IsNullOrEmpty(door.NameTag) || door is LabApi.Features.Wrappers.ElevatorDoor)
                continue;
            string zone = door.Zone.ToString();
            if (!doors.TryGetValue(zone, out object? list))
                doors[zone] = list = new List<object>();
            ((List<object>)list!).Add(new JsonObject
            {
                ["name"] = door.NameTag,
                ["open"] = door.IsOpened,
                ["locked"] = door.IsLocked,
            });
        }

        var elevators = Elevator.List.Where(e => e != null).Select(e => e.Group.ToString()).Distinct().OrderBy(g => g).ToList();

        var result = new JsonObject
        {
            ["zones"] = new[] { "LightContainment", "HeavyContainment", "Entrance", "Surface" },
            ["named_doors"] = doors,
            ["elevator_groups"] = elevators,
            ["teslas"] = Tesla.List.Count,
            ["warhead"] = Safe(() => new JsonObject
            {
                ["exists"] = Warhead.Exists,
                ["detonated"] = Warhead.IsDetonated,
                ["in_progress"] = Warhead.IsDetonationInProgress,
                ["locked"] = Warhead.IsLocked,
                ["lever_on"] = Warhead.LeverStatus,
            }),
            ["decontamination"] = Safe(() => Decontamination.Status.ToString()),
            ["waves"] = Safe(() => new JsonObject
            {
                ["mtf"] = Wave(RespawnWaves.PrimaryMtfWave),
                ["chaos"] = Wave(RespawnWaves.PrimaryChaosWave),
            }),
        };

        if (_ctx != null)
        {
            float left = _ctx.TeslasDisabledUntil - _ctx.Now;
            result["teslas_disabled_sec_left"] = left > 0 ? (int)left : 0;
        }
        return result;
    }

    private static object? Wave(RespawnWave? wave) =>
        wave == null
            ? null
            : new JsonObject
            {
                ["time_left_sec"] = (int)wave.TimeLeft,
                ["tokens"] = wave.RespawnTokens,
                ["paused"] = wave.IsForcefullyPaused,
            };

    private static object? Safe(Func<object?> build)
    {
        try
        {
            return build();
        }
        catch (Exception e)
        {
            return new JsonObject { ["error"] = e.Message };
        }
    }
}

public sealed class CassieExamplesTool : ITool
{
    private readonly CassieLibrary _library;
    private readonly DirectorContext _ctx;

    public CassieExamplesTool(CassieLibrary library, DirectorContext ctx)
    {
        _library = library;
        _ctx = ctx;
    }

    public string Name => "cassie_examples";
    public string Title => "CASSIE samples";

    public string Description =>
        "CASSIE announcement samples in the server style: Russian subtitles + spoken text in working syntax. "
        + "ids = ids from the index in the system prompt, or query = a word from the (Russian) situation label. Up to 4.";

    public JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["ids"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Sample ids",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
                ["query"] = Schema.Str("A word from the situation label, e.g. НОУС or боеголовка"),
            }
        );

    public object Execute(Dictionary<string, object?> args)
    {
        List<string>? ids = ToolArgs.StringList(args, "ids");
        string? query = ToolArgs.String(args, "query");
        if ((ids == null || ids.Count == 0) && string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("ids or query is required");

        var found = _library.Find(ids, query, _ctx.Config.Profile, 4);
        if (found.Count == 0)
            throw new ArgumentException("Nothing found - see the sample index in the system prompt");

        return new JsonObject
        {
            ["examples"] = found
                .Select(e =>
                {
                    var o = new JsonObject
                    {
                        ["id"] = e.Id,
                        ["situation"] = e.Situation,
                        ["subtitles"] = e.Subtitles,
                        ["spoken"] = e.Spoken,
                    };
                    if (e.Vars.Count > 0)
                        o["vars"] = e.Vars;
                    return (object)o;
                })
                .ToList(),
        };
    }
}
