using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public static class Conditions
{
    public const string Help =
        "when = optional condition object, ALL keys must hold at that second or the event is skipped: "
        + "role_alive / role_dead = role name (Scp049...), team_alive / team_dead = SCPs|FoundationForces|ChaosInsurgency|Scientists|ClassD, "
        + "zone_occupied / zone_empty = LightContainment|HeavyContainment|Entrance|Surface, min_alive = integer.";

    private static readonly string[] Keys = { "role_alive", "role_dead", "team_alive", "team_dead", "zone_occupied", "zone_empty", "min_alive" };

    public static JsonObject JsonSchema { get; } =
        new()
        {
            ["type"] = "object",
            ["description"] = "Optional condition, all keys must hold or the event is skipped",
            ["properties"] = new JsonObject
            {
                ["role_alive"] = new JsonObject { ["type"] = "string" },
                ["role_dead"] = new JsonObject { ["type"] = "string" },
                ["team_alive"] = new JsonObject { ["type"] = "string" },
                ["team_dead"] = new JsonObject { ["type"] = "string" },
                ["zone_occupied"] = new JsonObject { ["type"] = "string" },
                ["zone_empty"] = new JsonObject { ["type"] = "string" },
                ["min_alive"] = new JsonObject { ["type"] = "integer" },
            },
        };

    public static void Validate(Dictionary<string, object?> when)
    {
        foreach (string key in when.Keys)
        {
            if (Array.IndexOf(Keys, key) < 0)
                throw new ArgumentException($"when.{key}: unknown condition. {Help}");
        }
        RoleKey(when, "role_alive");
        RoleKey(when, "role_dead");
        TeamKey(when, "team_alive");
        TeamKey(when, "team_dead");
        ZoneKey(when, "zone_occupied");
        ZoneKey(when, "zone_empty");
        ToolArgs.Int(when, "min_alive", 0, 0, 100);
    }

    public static bool Check(Dictionary<string, object?>? when, out string reason)
    {
        reason = "";
        if (when == null || when.Count == 0)
            return true;

        List<Player> alive = PlayerInfo.RealPlayers().Where(p => p.IsAlive).ToList();

        RoleTypeId? role = RoleKey(when, "role_alive");
        if (role != null && !alive.Any(p => p.Role == role))
            return Fail($"нет живых {role}", out reason);
        role = RoleKey(when, "role_dead");
        if (role != null && alive.Any(p => p.Role == role))
            return Fail($"{role} жив", out reason);

        Team? team = TeamKey(when, "team_alive");
        if (team != null && !alive.Any(p => p.Team == team))
            return Fail($"нет живых {team}", out reason);
        team = TeamKey(when, "team_dead");
        if (team != null && alive.Any(p => p.Team == team))
            return Fail($"{team} ещё живы", out reason);

        FacilityZone? zone = ZoneKey(when, "zone_occupied");
        if (zone != null && !alive.Any(p => p.Zone == zone))
            return Fail($"в {zone} никого", out reason);
        zone = ZoneKey(when, "zone_empty");
        if (zone != null && alive.Any(p => p.Zone == zone))
            return Fail($"в {zone} есть люди", out reason);

        int min = ToolArgs.Int(when, "min_alive", 0, 0, 100);
        if (alive.Count < min)
            return Fail($"живых {alive.Count} < {min}", out reason);
        return true;
    }

    public static string Describe(Dictionary<string, object?>? when) =>
        when == null || when.Count == 0 ? "" : " if " + string.Join(", ", when.Select(p => $"{p.Key}={p.Value}"));

    private static bool Fail(string why, out string reason)
    {
        reason = why;
        return false;
    }

    private static RoleTypeId? RoleKey(Dictionary<string, object?> when, string key)
    {
        string? value = ToolArgs.String(when, key);
        if (value == null)
            return null;
        if (!Enum.TryParse(value.Trim(), true, out RoleTypeId role) || role == RoleTypeId.None)
            throw new ArgumentException($"when.{key}: unknown role '{value}'");
        return role;
    }

    private static Team? TeamKey(Dictionary<string, object?> when, string key)
    {
        string? value = ToolArgs.String(when, key);
        if (value == null)
            return null;
        if (!Enum.TryParse(value.Trim(), true, out Team team))
            throw new ArgumentException($"when.{key}: unknown team '{value}', allowed: {string.Join(", ", Enum.GetNames(typeof(Team)))}");
        return team;
    }

    private static FacilityZone? ZoneKey(Dictionary<string, object?> when, string key)
    {
        string? value = ToolArgs.String(when, key);
        if (value == null)
            return null;
        return ActionTool.ParseZone(value) ?? throw new ArgumentException($"when.{key}: a single zone, not 'all'");
    }
}
