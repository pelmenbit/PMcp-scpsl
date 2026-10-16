using System;
using System.Collections.Generic;
using System.Linq;
using PlayerRoles;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class TimelineEntry
{
    public int AtSec { get; set; }
    public string Action { get; set; } = "";
    public Dictionary<string, object?> Args { get; set; } = new();
    public Dictionary<string, object?>? When { get; set; }
    public string Note { get; set; } = "";

    public string Describe() =>
        $"  {AtSec,5} с  {Action} {TextSanitizer.StripTags(Json.Write(Args))}{Conditions.Describe(When)}{(Note.Length > 0 ? "  // " + Note : "")}";
}

public sealed class RoundPlan
{
    public string Theme { get; set; } = "";
    public string Summary { get; set; } = "";
    public Dictionary<int, RoleTypeId> Roles { get; } = new();

    public Dictionary<RoleTypeId, int> RoleMix { get; } = new();
    public List<TimelineEntry> Timeline { get; } = new();

    public string Describe()
    {
        var lines = new List<string> { $"Тема: {Theme}", Summary };
        if (Roles.Count > 0)
            lines.Add("Роли: " + string.Join(", ", Roles.Select(r => $"{r.Key}->{r.Value}")));
        if (RoleMix.Count > 0)
            lines.Add("Остальным: " + string.Join(", ", RoleMix.Select(r => $"{r.Key} {r.Value}")));
        foreach (TimelineEntry e in Timeline.OrderBy(e => e.AtSec))
            lines.Add(e.Describe());
        return string.Join("\n", lines.Where(l => !string.IsNullOrEmpty(l)));
    }
}

public sealed class PlanRoundTool : ITool
{
    private readonly DirectorContext _ctx;
    private readonly Dictionary<string, ActionTool> _actions = new(StringComparer.OrdinalIgnoreCase);

    public PlanRoundTool(DirectorContext ctx, IEnumerable<ActionTool> actions)
    {
        _ctx = ctx;
        foreach (ActionTool action in actions)
            _actions[action.Name] = action;

        Description =
            "Submit the whole round plan: theme, short summary, starting roles - one for EVERY player in the lobby list, "
            + "role_mix - shares of human roles for players who join later, and the timeline - "
            + "a list of {at_sec, action, args, when?, note}. at_sec = round second, action = action name, args = an object "
            + "with that action's arguments exactly as listed below (? = optional, a|b = one of the values). "
            + Conditions.Help + " "
            + "Call it once; if it returns an error, fix that and call again. Event example: "
            + "{\"at_sec\":240,\"action\":\"lights\",\"args\":{\"action\":\"off\",\"zone\":\"LightContainment\",\"duration_sec\":30}}. "
            + "Conditional example: {\"at_sec\":420,\"action\":\"cassie\",\"args\":{\"template\":\"tpl_scp_breach\",\"vars\":{\"scp\":\"049\"}},\"when\":{\"role_alive\":\"Scp049\"}}. "
            + "Roles example: [{\"player_id\":2,\"role\":\"Scp173\"},{\"player_id\":3,\"role\":\"ClassD\"}]. "
            + "role_mix example: [{\"role\":\"ClassD\",\"weight\":60},{\"role\":\"Scientist\",\"weight\":25},{\"role\":\"FacilityGuard\",\"weight\":15}]\n"
            + "Actions:\n"
            + string.Join("\n", _actions.Values.Select(Signature));
    }

    public string Name => "plan_round";
    public string Title => "Submit round plan";
    public string Description { get; }

    internal static string Signature(ActionTool tool)
    {
        var required = new HashSet<string>(
            tool.InputSchema.TryGetValue("required", out object? r) && r is string[] names ? names : Array.Empty<string>()
        );
        var parts = new List<string>();
        if (tool.InputSchema.TryGetValue("properties", out object? p) && p is JsonObject properties)
        {
            foreach (KeyValuePair<string, object?> prop in properties)
            {
                string name = prop.Key + (required.Contains(prop.Key) ? "" : "?");
                if (prop.Value is JsonObject schema && schema.TryGetValue("enum", out object? e) && e is string[] values)
                    parts.Add($"{name}={string.Join("|", values)}");
                else if (prop.Value is JsonObject typed && typed.TryGetValue("type", out object? t))
                    parts.Add($"{name}:{t}");
                else
                    parts.Add(name);
            }
        }
        return $"- {tool.Name}: {string.Join(", ", parts)}";
    }

    internal static JsonObject TimelineSchema(string description) =>
        new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["at_sec"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 3600 },
                    ["action"] = Schema.Str("Action name"),
                    ["args"] = new JsonObject { ["type"] = "object", ["description"] = "Action arguments" },
                    ["when"] = Conditions.JsonSchema,
                    ["note"] = Schema.Str("Why this event"),
                },
                ["required"] = new[] { "at_sec", "action", "args" },
            },
        };

    public JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["theme"] = Schema.Str("Round theme, short (Russian)"),
                ["summary"] = Schema.Str("Story in 1-3 sentences (Russian)"),
                ["roles"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Starting roles, one per lobby player",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["player_id"] = new JsonObject { ["type"] = "integer" },
                            ["role"] = Schema.Str("Role", SetRoleActionTool.AllowedRoleNames),
                        },
                        ["required"] = new[] { "player_id", "role" },
                    },
                },
                ["role_mix"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Shares of human start roles for players not listed in roles (late joiners)",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["role"] = Schema.Str("Human start role"),
                            ["weight"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 },
                        },
                        ["required"] = new[] { "role", "weight" },
                    },
                },
                ["timeline"] = TimelineSchema("Round events"),
            },
            "theme",
            "roles",
            "timeline"
        );

    public object Execute(Dictionary<string, object?> args)
    {
        DirectorConfig cfg = _ctx.Config;
        bool forStart = _ctx.PlanForStart;
        _ctx.PlanStats.Attempts++;
        var plan = new RoundPlan
        {
            Theme = TextSanitizer.Clean(ToolArgs.String(args, "theme"), 120) ?? "",
            Summary = TextSanitizer.Clean(ToolArgs.String(args, "summary"), 400) ?? "",
        };
        var dropped = new List<string>();
        var fixedNotes = new List<string>();
        List<RoleTypeId> humanRoles = StartHumanRoles(cfg);
        int maxScps = _ctx.ScpLimit(_ctx.PlanPlayers.Count);

        if (args.TryGetValue("roles", out object? r) && r is List<object?> roles)
        {
            int scps = 0;
            foreach (object? item in roles)
            {
                if (item is not Dictionary<string, object?> o)
                    continue;
                try
                {
                    int id = ToolArgs.Int(o, "player_id", -1, 0, int.MaxValue);
                    RoleTypeId role = SetRoleActionTool.ParseRole(ToolArgs.String(o, "role"));
                    bool scp = SetRoleActionTool.IsScp(role);
                    if (!_ctx.PlanPlayers.ContainsKey(id))
                        dropped.Add($"role for player {id}: not in the lobby");
                    else if (scp && scps >= maxScps)
                        dropped.Add($"role {role} for player {id}: more than {maxScps} SCPs for {_ctx.PlanPlayers.Count} players");
                    else if (forStart && !scp && !humanRoles.Contains(role))
                        dropped.Add($"role {role} for player {id}: at round start only SCPs and {string.Join(", ", humanRoles)}");
                    else if (!plan.Roles.ContainsKey(id))
                    {
                        plan.Roles[id] = role;
                        if (scp)
                            scps++;
                    }
                }
                catch (ArgumentException e)
                {
                    dropped.Add("role: " + e.Message);
                }
            }
        }

        ParseRoleMix(args, humanRoles, plan, dropped);

        if (!args.TryGetValue("timeline", out object? t) || t is not List<object?> timeline || timeline.Count == 0)
            throw new ArgumentException("Plan rejected: timeline is required - a non-empty array of events");

        plan.Timeline.AddRange(ParseTimeline(timeline, 0, cfg.MaxTimelineEntries, _ctx, _actions, dropped, fixedNotes));

        if (cfg.IntroMinEvents > 0 && forStart)
        {
            int intro = plan.Timeline.Count(e => e.AtSec <= 60);
            if (intro < cfg.IntroMinEvents)
                throw new ArgumentException(
                    $"Plan rejected: {intro} events in the first 60 s, at least {cfg.IntroMinEvents} required - the opening: "
                        + "CASSIE wakes up (standby, self-diagnostic, system scan), lights flicker zone by zone, "
                        + "\"all systems stable\". Samples: intro_*. Keep the rest of the plan as is, just add the opening."
                );
        }
        if (cfg.AssignRoles && forStart && _ctx.PlanPlayers.Count > 0)
        {
            var missing = _ctx.PlanPlayers.Keys.Where(id => !plan.Roles.ContainsKey(id)).OrderBy(id => id).ToList();
            if (missing.Count > 0)
            {
                var roleProblems = dropped.Where(d => d.StartsWith("role", StringComparison.Ordinal)).ToList();
                throw new ArgumentException(
                    $"Plan rejected: no role for lobby players: {string.Join(", ", missing.Select(id => $"{id} ({_ctx.PlanPlayers[id]})"))}. "
                        + $"Every one of the {_ctx.PlanPlayers.Count} players needs a role: at most {maxScps} SCPs, "
                        + $"humans only {string.Join(", ", humanRoles)}."
                        + (roleProblems.Count > 0 ? "\n- " + string.Join("\n- ", roleProblems) : "")
                );
            }

            var repeated = plan.Roles.Where(p => SetRoleActionTool.IsScp(p.Value) && _ctx.RecentScps.Contains(p.Key)).Select(p => p.Key).ToList();
            var fresh = plan.Roles.Where(p => !SetRoleActionTool.IsScp(p.Value) && !_ctx.RecentScps.Contains(p.Key)).Select(p => p.Key).ToList();
            if (repeated.Count > 0 && fresh.Count >= repeated.Count)
                throw new ArgumentException(
                    $"Plan rejected: players {string.Join(", ", repeated)} were SCP in the previous round (recent_scp) - "
                        + $"give their SCP roles to other players, e.g. {string.Join(", ", fresh.Take(repeated.Count + 2))}, keep the rest of the plan."
                );
        }

        if (plan.Timeline.Count == 0 || plan.Timeline.Count * 2 < timeline.Count)
            throw new ArgumentException(
                $"Plan rejected: only {plan.Timeline.Count} of {timeline.Count} events are valid.\n- " + string.Join("\n- ", dropped)
            );

        _ctx.PlanStats.Dropped = dropped.Count;
        _ctx.PlanStats.Fixed = fixedNotes.Count;
        _ctx.PlanAccepted?.Invoke(plan);
        _ctx.Log(
            $"план принят{(_ctx.PlanningAhead ? " (на следующий раунд)" : "")}: событий {plan.Timeline.Count} из {timeline.Count}, ролей {plan.Roles.Count}"
                + (fixedNotes.Count > 0 ? "\n  исправлено: " + string.Join("\n  исправлено: ", fixedNotes) : "")
                + (dropped.Count > 0 ? "\n  отброшено: " + string.Join("\n  отброшено: ", dropped) : "")
        );
        return new JsonObject
        {
            ["accepted"] = true,
            ["roles"] = plan.Roles.Count,
            ["role_mix"] = MixJson(plan.RoleMix),
            ["timeline"] = plan.Timeline.Count,
            ["fixed"] = fixedNotes,
            ["dropped"] = dropped,
            ["note"] = "Plan accepted. Do not submit it again.",
        };
    }

    private static JsonObject MixJson(Dictionary<RoleTypeId, int> mix)
    {
        var o = new JsonObject();
        foreach (KeyValuePair<RoleTypeId, int> pair in mix)
            o[pair.Key.ToString()] = pair.Value;
        return o;
    }

    public static List<RoleTypeId> StartHumanRoles(DirectorConfig cfg)
    {
        var roles = new List<RoleTypeId>();
        foreach (string name in cfg.StartHumanRoles)
        {
            if (Enum.TryParse(name?.Trim(), true, out RoleTypeId role) && !SetRoleActionTool.IsScp(role) && role != RoleTypeId.None && !roles.Contains(role))
                roles.Add(role);
        }
        if (roles.Count == 0)
            roles.Add(RoleTypeId.ClassD);
        return roles;
    }

    private static void ParseRoleMix(Dictionary<string, object?> args, List<RoleTypeId> allowed, RoundPlan plan, List<string> dropped)
    {
        var pairs = new List<(string? Role, object? Weight)>();
        if (args.TryGetValue("role_mix", out object? m))
        {
            if (m is List<object?> list)
                pairs.AddRange(list.OfType<Dictionary<string, object?>>().Select(o => (ToolArgs.String(o, "role"), o.TryGetValue("weight", out object? w) ? w : null)));
            else if (m is Dictionary<string, object?> map)
                pairs.AddRange(map.Select(p => ((string?)p.Key, p.Value)));
        }

        foreach ((string? name, object? weight) in pairs)
        {
            try
            {
                RoleTypeId role = SetRoleActionTool.ParseRole(name);
                if (!allowed.Contains(role))
                    throw new ArgumentException($"{role} is not a human start role ({string.Join(", ", allowed)})");
                int w = ToolArgs.Int(new Dictionary<string, object?> { ["weight"] = weight }, "weight", 1, 1, 100);
                plan.RoleMix[role] = w;
            }
            catch (ArgumentException e)
            {
                dropped.Add("role_mix: " + e.Message);
            }
        }

        if (plan.RoleMix.Count > 0)
            return;
        foreach (RoleTypeId role in plan.Roles.Values.Where(allowed.Contains))
            plan.RoleMix[role] = plan.RoleMix.TryGetValue(role, out int n) ? n + 1 : 1;
        if (plan.RoleMix.Count == 0)
            plan.RoleMix[allowed[0]] = 1;
    }

    internal static List<TimelineEntry> ParseTimeline(
        List<object?> timeline,
        int minSec,
        int maxEntries,
        DirectorContext ctx,
        Dictionary<string, ActionTool> actions,
        List<string> dropped,
        List<string> fixedNotes
    )
    {
        var result = new List<TimelineEntry>();
        for (int i = 0; i < timeline.Count; i++)
        {
            if (result.Count >= maxEntries)
            {
                dropped.Add($"#{i} and later: more than {maxEntries} events");
                break;
            }
            if (timeline[i] is not Dictionary<string, object?> o)
            {
                dropped.Add($"#{i}: not an object");
                continue;
            }

            o.TryGetValue("at_sec", out object? at);
            string label = $"#{i} ({(o.TryGetValue("action", out object? n) ? n as string : null) ?? "?"} at {Json.Write(at)} s)";
            try
            {
                var entry = new TimelineEntry
                {
                    AtSec = ToolArgs.Int(o, "at_sec", -1, 0, 3600),
                    Action = ToolArgs.String(o, "action")?.Trim() ?? "",
                    Note = TextSanitizer.Clean(ToolArgs.String(o, "note"), 120) ?? "",
                };
                if (entry.AtSec < minSec)
                    throw new ArgumentException($"at_sec {entry.AtSec} is too early, the earliest allowed second is {minSec}");
                if (o.TryGetValue("args", out object? a) && a is Dictionary<string, object?> actionArgs)
                    entry.Args = actionArgs;
                if (o.TryGetValue("when", out object? w) && w is Dictionary<string, object?> when && when.Count > 0)
                {
                    Conditions.Validate(when);
                    entry.When = when;
                }

                if (!actions.TryGetValue(entry.Action, out ActionTool tool))
                    throw new ArgumentException($"unknown action '{entry.Action}', allowed: {string.Join(", ", actions.Keys)}");
                if (!ctx.IsAllowed(tool.Name))
                    throw new ArgumentException($"action {tool.Name} is disabled in the config");

                string? note = tool.Name == "cassie" ? StripUnknownWords(entry.Args) : null;
                tool.Prepare(entry.Args);
                if (note != null)
                    fixedNotes.Add($"{label}: {note}");
                result.Add(entry);
            }
            catch (ArgumentException e)
            {
                dropped.Add($"{label}: {e.Message}");
            }
        }
        result.Sort((x, y) => x.AtSec.CompareTo(y.AtSec));
        return result;
    }

    private static string? StripUnknownWords(Dictionary<string, object?> args)
    {
        if (!CassieKit.Available || args.ContainsKey("template") || !args.TryGetValue("spoken", out object? s) || s is not string raw)
            return null;

        string spoken = CassieKit.Normalize(raw, new List<string>());
        List<string> unknown = CassieKit.UnknownTokens(spoken);
        if (unknown.Count == 0)
            return null;

        string[] tokens = spoken.Split(' ');
        int words = tokens.Count(w => w.Length > 0 && char.IsLetter(w[0]) && !CassieKit.IsModifier(w));
        int bad = tokens.Count(unknown.Contains);
        if (words == 0 || bad * 10 > words * 4 || words - bad < 3)
            return null;

        args["spoken"] = string.Join(" ", tokens.Where(w => !unknown.Contains(w)));
        return "removed words not in the vocabulary: " + CassieKit.DescribeUnknown(unknown);
    }
}
