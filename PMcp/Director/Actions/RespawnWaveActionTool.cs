using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class RespawnWaveActionTool : ActionTool
{
    private static readonly string[] Actions = { "spawn_now", "start_timer", "hold", "release" };
    private static readonly string[] Factions = { "mtf", "chaos" };

    public RespawnWaveActionTool(DirectorContext ctx)
        : base(ctx) { }

    public override string Name => "respawn_wave";
    public override string Title => "MTF / Chaos waves";

    public override string Description =>
        "Reinforcement wave: spawn_now = right now, start_timer = start the arrival (with animation), "
        + "hold = pause the timer for duration_sec (releases itself), release = unpause.";

    public override JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["action"] = Schema.Str("What to do", Actions),
                ["faction"] = Schema.Str("Whose wave", Factions),
                ["duration_sec"] = Schema.Int("Hold for this many seconds", 1, 3600, 120),
            },
            "action",
            "faction"
        );

    public override PreparedAction Prepare(Dictionary<string, object?> args)
    {
        string action = OneOf(args, "action", Actions);
        string faction = OneOf(args, "faction", Factions);
        int duration = ToolArgs.Int(args, "duration_sec", 120, 1, 3600);
        if (action == "hold" && duration > Ctx.Config.MaxLockSeconds * 2)
            throw new ArgumentException($"duration_sec max is {Ctx.Config.MaxLockSeconds * 2}");

        return new PreparedAction
        {
            Summary = action == "hold" ? $"{faction}: hold for {duration} s" : $"{faction}: {action}",
            Apply = () =>
            {
                RespawnWave? wave = faction == "mtf" ? RespawnWaves.PrimaryMtfWave : RespawnWaves.PrimaryChaosWave;
                if (wave == null)
                    throw new ArgumentException("Wave is unavailable (is the round running?)");

                switch (action)
                {
                    case "spawn_now":
                        wave.InstantRespawn();
                        break;
                    case "start_timer":
                        wave.InitiateRespawn();
                        break;
                    case "hold":
                        wave.IsForcefullyPaused = true;
                        Ctx.Schedule(
                            duration,
                            $"отпустить волну {faction}",
                            () =>
                            {
                                wave.IsForcefullyPaused = false;
                                Ctx.Log($"respawn_wave: {faction} отпущена");
                            }
                        );
                        break;
                    case "release":
                        wave.IsForcefullyPaused = false;
                        break;
                }
            },
        };
    }
}
