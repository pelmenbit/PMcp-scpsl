using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using PMcp.Util;

namespace PMcp.Tools;

public sealed class ServerStatusTool : ITool
{
    public string Name => "server_status";
    public string Title => "Server status";

    public string Description =>
        "SCP:SL server snapshot right now: online, TPS, round state and stats, "
        + "alive players per team, generators, warhead, LCZ decontamination. No arguments. "
        + "server.name is set by the server owner.";

    public JsonObject InputSchema { get; } = Schema.Object(new JsonObject());

    public object Execute(Dictionary<string, object?> args)
    {
        List<Player> players = PlayerInfo.RealPlayers();

        return new JsonObject
        {
            ["server"] = Section(() => new JsonObject
            {
                ["name"] = TextSanitizer.Clean(Server.ServerListName, 128),
                ["port"] = (int)Server.Port,
                ["online"] = players.Count,
                ["max_players"] = Server.MaxPlayers,
                ["tps"] = Math.Round(Server.Tps, 1),
                ["max_tps"] = (int)Server.MaxTps,
                ["friendly_fire"] = Server.FriendlyFire,
                ["idle_mode"] = Server.IdleModeActive,
            }),
            ["round"] = Section(BuildRound),
            ["teams"] = Section(() => BuildTeams(players)),
            ["generators"] = Section(BuildGenerators),
            ["warhead"] = Section(BuildWarhead),
            ["decontamination"] = Section(() => new JsonObject
            {
                ["status"] = Decontamination.Status.ToString(),
                ["active"] = Decontamination.IsDecontaminating,
            }),
        };
    }

    private static object BuildRound()
    {
        string state = Round.IsRoundEnded ? "ended" : Round.IsRoundStarted ? "in_progress" : "waiting";
        var o = new JsonObject
        {
            ["state"] = state,
            ["locked"] = Round.IsLocked,
            ["lobby_locked"] = Round.IsLobbyLocked,
        };
        if (!Round.IsRoundStarted)
            return o;

        o["duration_sec"] = (long)Round.Duration.TotalSeconds;
        o["total_deaths"] = Round.TotalDeaths;
        o["killed_by_scps"] = Round.KilledBySCPs;
        o["escaped_class_d"] = Round.EscapedClassD;
        o["escaped_scientists"] = Round.EscapedScientists;
        o["surviving_scps"] = Round.SurvivingSCPs;
        o["changed_into_zombies"] = Round.ChangedIntoZombies;
        return o;
    }

    private static object BuildTeams(List<Player> players)
    {
        var alive = new JsonObject();
        int dead = 0;
        foreach (Player p in players)
        {
            if (!p.IsAlive)
            {
                dead++;
                continue;
            }
            string team = p.Team.ToString();
            alive[team] = alive.TryGetValue(team, out object? n) ? (int)n! + 1 : 1;
        }
        return new JsonObject { ["alive"] = alive, ["dead_or_spectating"] = dead };
    }

    private static object BuildGenerators()
    {
        int total = 0;
        int engaged = 0;
        int activating = 0;
        foreach (Generator generator in Generator.List)
        {
            total++;
            if (generator.Engaged)
                engaged++;
            else if (generator.Activating)
                activating++;
        }
        return new JsonObject
        {
            ["total"] = total,
            ["engaged"] = engaged,
            ["activating"] = activating,
        };
    }

    private static object BuildWarhead()
    {
        if (!Warhead.Exists)
            return new JsonObject { ["exists"] = false };

        var o = new JsonObject
        {
            ["detonated"] = Warhead.IsDetonated,
            ["in_progress"] = Warhead.IsDetonationInProgress,
            ["lever_on"] = Warhead.LeverStatus,
            ["locked"] = Warhead.IsLocked,
        };
        if (Warhead.IsDetonationInProgress)
            o["time_left_sec"] = Math.Round(Warhead.DetonationTime, 1);
        return o;
    }

    private static object Section(Func<object> build)
    {
        try
        {
            return build();
        }
        catch (Exception e)
        {
            return new JsonObject { ["error"] = e.GetType().Name + ": " + e.Message };
        }
    }
}
