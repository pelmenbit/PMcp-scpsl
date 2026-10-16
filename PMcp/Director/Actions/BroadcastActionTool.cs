using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class BroadcastActionTool : ActionTool
{
    public BroadcastActionTool(DirectorContext ctx)
        : base(ctx) { }

    public override string Name => "broadcast";
    public override string Title => "Broadcast";

    public override string Description =>
        "On-screen text IN RUSSIAN (rich text). target: all (default), a team (SCPs, FoundationForces, "
        + "ChaosInsurgency, Scientists, ClassD, Dead) or a numeric player id. zone = only alive players in that zone "
        + "(\"all staff of LCZ\"). style: broadcast = big text at the top (default), hint = small text, for minor messages. "
        + "duration = seconds (1-20).";

    public override JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["text"] = Schema.Str("Broadcast text in Russian, rich text allowed"),
                ["duration"] = Schema.Int("Seconds on screen", 1, 20, 10),
                ["target"] = Schema.Str("all | team | player id"),
                ["zone"] = Schema.Str("Only alive players in this zone", ZoneNames),
                ["style"] = Schema.Str("broadcast or hint", Styles),
                ["clear"] = Schema.Bool("Clear previous broadcasts", false),
            },
            "text"
        );

    private static readonly string[] Styles = { "broadcast", "hint" };

    public override PreparedAction Prepare(Dictionary<string, object?> args)
    {
        string text = TextSanitizer.SafeRichText(Required(args, "text").Replace("\\n", "\n"));
        int visible = TextSanitizer.StripTags(text).Length;
        if (visible > Ctx.Config.BroadcastMaxLength)
            throw new ArgumentException($"Broadcast is {visible} characters, max {Ctx.Config.BroadcastMaxLength}");

        ushort duration = (ushort)ToolArgs.Int(args, "duration", 10, 1, 20);
        bool clear = ToolArgs.Bool(args, "clear", false);
        string target = ToolArgs.String(args, "target")?.Trim() ?? "all";
        bool hint = OneOf(args, "style", Styles, "broadcast") == "hint";
        FacilityZone? zone = ParseZone(ToolArgs.String(args, "zone"));

        Team? team = null;
        int? playerId = null;
        if (int.TryParse(target, out int id))
            playerId = id;
        else if (!string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse(target, true, out Team parsed))
                throw new ArgumentException($"target '{target}': use all, a player id or a team ({string.Join(", ", Enum.GetNames(typeof(Team)))})");
            team = parsed;
        }

        return new PreparedAction
        {
            Summary = $"{(hint ? "hint " : "")}{target}{(zone != null ? " in " + zone : "")}, {duration} s: {TextSanitizer.Clean(text, 90)}",
            Apply = () =>
            {
                if (!hint && team == null && playerId == null && zone == null)
                {
                    Server.SendBroadcast(text, duration, global::Broadcast.BroadcastFlags.Normal, clear);
                    return;
                }

                int sent = 0;
                foreach (Player p in PlayerInfo.RealPlayers())
                {
                    if ((playerId != null && p.PlayerId != playerId) || (team != null && p.Team != team))
                        continue;
                    if (zone != null && (!p.IsAlive || p.Zone != zone))
                        continue;
                    if (hint)
                        p.SendHint(text, duration);
                    else
                        p.SendBroadcast(text, duration, global::Broadcast.BroadcastFlags.Normal, clear);
                    sent++;
                }
                if (sent == 0)
                    throw new ArgumentException($"No recipients for target '{target}'{(zone != null ? " in " + zone : "")}");
            },
        };
    }
}
