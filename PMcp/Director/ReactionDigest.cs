using System;
using System.Collections.Generic;
using System.Linq;
using PMcp.Util;

namespace PMcp.Director;

public sealed class ReactionDigest
{
    public static readonly string[] AllTriggers =
    {
        "scp_death",
        "escape",
        "wave",
        "team_wiped",
        "mass_casualty",
        "generators",
        "warhead",
        "decontamination",
        "scp079_recontained",
        "cuffed",
    };

    public List<string> Triggers { get; } = new();
    public JsonObject Summary { get; } = new();

    public static ReactionDigest Build(
        List<JsonObject> events,
        Dictionary<string, int> aliveBefore,
        Dictionary<string, int> aliveNow,
        DirectorConfig cfg,
        long nowUnix
    )
    {
        var digest = new ReactionDigest();
        bool On(string trigger) => cfg.ReactionTriggers.Exists(t => string.Equals(t, trigger, StringComparison.OrdinalIgnoreCase));

        var deathsByRole = new Dictionary<string, int>();
        var deathsByTeam = new Dictionary<string, int>();
        var deathsByReason = new Dictionary<string, int>();
        int recentDeaths = 0;
        var cuffs = new Dictionary<string, int>();

        foreach (JsonObject e in events)
        {
            string type = Str(e, "type");
            switch (type)
            {
                case "death":
                    string role = Str(e, "role");
                    Count(deathsByRole, role);
                    Count(deathsByTeam, Str(e, "team"));
                    Count(deathsByReason, Str(e, "reason"));
                    if (e.TryGetValue("time", out object? t) && t is long time && nowUnix - time <= cfg.MassCasualtyWindowSeconds)
                        recentDeaths++;

                    if (On("scp_death") && role.StartsWith("Scp", StringComparison.Ordinal) && role != "Scp0492")
                    {
                        string killer = Str(e, "attacker_role");
                        digest.Triggers.Add($"{role} died{(killer.Length > 0 ? $", killed by {killer}" : "")}: {Short(Str(e, "reason"))}");
                    }
                    break;

                case "escaped":
                    if (On("escape"))
                        digest.Triggers.Add($"Escape: {Str(e, "old_role")} -> {Str(e, "new_role")}");
                    break;

                case "wave_respawned":
                    if (On("wave"))
                    {
                        int count = e.TryGetValue("players", out object? p) && p is List<object?> players ? players.Count : 0;
                        digest.Triggers.Add($"Wave arrived: {Str(e, "faction")}, {count} players");
                    }
                    break;

                case "generator_activated":
                    if (On("generators"))
                    {
                        long engaged = Num(e, "engaged");
                        long generators = Num(e, "total");
                        digest.Triggers.Add(
                            engaged >= generators && generators > 0
                                ? $"All {generators} generators engaged - SCP-079 recontainment begins"
                                : $"Generator activated ({engaged}/{generators} engaged) in {Str(e, "room")}"
                        );
                    }
                    break;

                case "warhead_started":
                    if (On("warhead"))
                        digest.Triggers.Add(e.TryGetValue("automatic", out object? a) && a is true ? "Warhead started automatically" : "Warhead started by a player");
                    break;

                case "warhead_stopped":
                    if (On("warhead"))
                        digest.Triggers.Add("Warhead detonation cancelled");
                    break;

                case "warhead_detonated":
                    if (On("warhead"))
                        digest.Triggers.Add("Warhead detonated");
                    break;

                case "decontamination_started":
                    if (On("decontamination"))
                        digest.Triggers.Add("LCZ decontamination started");
                    break;

                case "scp079_recontained":
                    if (On("scp079_recontained"))
                        digest.Triggers.Add("SCP-079 recontained");
                    break;

                case "cuffed":
                    Count(cuffs, $"{Str(e, "role")} by {Str(e, "by_role")}");
                    break;
            }
        }

        if (On("cuffed") && cuffs.Count > 0)
            digest.Triggers.Add("Cuffed: " + Top(cuffs));

        if (On("mass_casualty") && recentDeaths >= cfg.MassCasualtyDeaths)
            digest.Triggers.Add(
                $"Mass casualties: {recentDeaths} in {cfg.MassCasualtyWindowSeconds} s ({Top(deathsByRole)}; {Top(deathsByReason)})"
            );

        if (On("team_wiped"))
        {
            foreach (KeyValuePair<string, int> before in aliveBefore)
            {
                if (before.Value > 0 && (!aliveNow.TryGetValue(before.Key, out int now) || now == 0) && deathsByTeam.ContainsKey(before.Key))
                    digest.Triggers.Add($"Team {before.Key} wiped out (was alive: {before.Value})");
            }
        }

        digest.Summary["triggers"] = digest.Triggers;
        int total = deathsByRole.Values.Sum();
        if (total > 0)
        {
            digest.Summary["deaths"] = new JsonObject
            {
                ["total"] = total,
                ["by_role"] = ToJson(deathsByRole),
                ["by_reason"] = ToJson(deathsByReason),
            };
        }
        digest.Summary["alive_now"] = ToJson(aliveNow);
        return digest;
    }

    private static long Num(JsonObject e, string key) =>
        e.TryGetValue(key, out object? v) ? v switch { int i => i, long l => l, double d => (long)d, _ => 0 } : 0;

    private static string Str(JsonObject e, string key) => e.TryGetValue(key, out object? v) && v is string s ? s : "";

    private static void Count(Dictionary<string, int> map, string key)
    {
        if (key.Length == 0)
            key = "?";
        map[key] = map.TryGetValue(key, out int n) ? n + 1 : 1;
    }

    private static string Top(Dictionary<string, int> map) =>
        string.Join(", ", map.OrderByDescending(p => p.Value).Take(3).Select(p => $"{Short(p.Key)} {p.Value}"));

    private static string Short(string text) => text.Length > 60 ? text.Substring(0, 60) + "…" : text;

    private static JsonObject ToJson(Dictionary<string, int> map)
    {
        var o = new JsonObject();
        foreach (KeyValuePair<string, int> pair in map.OrderByDescending(p => p.Value))
            o[pair.Key] = pair.Value;
        return o;
    }
}
