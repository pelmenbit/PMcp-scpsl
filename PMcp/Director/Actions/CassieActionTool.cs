using System;
using System.Collections.Generic;
using System.Linq;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class CassieActionTool : ActionTool
{
    private readonly CassieLibrary _library;

    public CassieActionTool(DirectorContext ctx, CassieLibrary library)
        : base(ctx)
    {
        _library = library;
    }

    public override string Name => "cassie";
    public override string Title => "C.A.S.S.I.E. announcement";

    public override string Description =>
        "Facility-wide CASSIE announcement. Either template = a sample id (+ vars for samples with {placeholders}) - "
        + "the voice is taken from the sample and is always valid; or spoken = your own voice text: only CASSIE vocabulary words, numbers, "
        + "modifiers ($PITCH_x, $SPAC_x, $SLEEP_x, $STUTT_a_b_n) and sounds (.G1-.G7, BELL_START, BELL_END). "
        + "Words outside the vocabulary are rejected with a list - replace them and retry. subtitles = on-screen text for players, IN RUSSIAN "
        + "(rich text <color=#hex>, <b>, line break \\n); with a template they default to the sample's subtitles. "
        + "background = alert tone before the announcement.";

    public override JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["template"] = Schema.Str("Sample id from the index (tpl_* take vars)"),
                ["vars"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Template values: scp = number (\"049\"), zone = LightContainment|HeavyContainment|Entrance|Surface, "
                        + "nato = one letter A-Z, anything else = integer 0-999",
                },
                ["spoken"] = Schema.Str("Spoken English, CASSIE vocabulary words only (when no template)"),
                ["subtitles"] = Schema.Str("Subtitles for players in Russian, rich text allowed"),
                ["background"] = Schema.Bool("Alert tone before the announcement", true),
            }
        );

    public override PreparedAction Prepare(Dictionary<string, object?> args)
    {
        string mode = (Ctx.Config.CassieMode ?? "mixed").Trim().ToLowerInvariant();
        string? templateId = ToolArgs.String(args, "template")?.Trim();
        string rawSpoken;
        string? templateSubtitles = null;
        if (!string.IsNullOrEmpty(templateId))
        {
            if (mode == "free")
                throw new ArgumentException("Templates are disabled on this server - write spoken yourself");
            CassieEntry entry = _library.Get(templateId!) ?? throw new ArgumentException($"No sample '{templateId}' - see the sample index");
            Dictionary<string, object?>? vars = args.TryGetValue("vars", out object? v) ? v as Dictionary<string, object?> : null;
            (rawSpoken, templateSubtitles) = CassieTemplates.Render(entry, vars);
        }
        else
        {
            if (mode == "templates")
                throw new ArgumentException("Free spoken text is disabled on this server - use template (+ vars)");
            rawSpoken = ToolArgs.String(args, "spoken")?.Trim() ?? "";
            if (rawSpoken.Length == 0)
                throw new ArgumentException("template or spoken is required");
        }

        var fixes = new List<string>();
        string spoken = CassieKit.Normalize(rawSpoken, fixes);
        if (spoken.Length == 0)
            throw new ArgumentException("spoken is empty after removing tags");

        List<string> unknown = CassieKit.UnknownTokens(spoken);
        if (unknown.Count > 0 && !string.IsNullOrEmpty(templateId))
        {
            spoken = string.Join(" ", spoken.Split(' ').Where(w => !unknown.Contains(w)));
            fixes.Add("removed: " + string.Join(" ", unknown));
            unknown.Clear();
        }
        if (unknown.Count > 0)
            throw new ArgumentException(
                $"Not in the CASSIE vocabulary: {CassieKit.DescribeUnknown(unknown)}. The game would silently skip them - replace with vocabulary words or remove."
            );

        bool background = ToolArgs.Bool(args, "background", true);
        double duration = CassieKit.Available ? CassieKit.Duration(spoken, background) : 0;
        if (duration > Ctx.Config.CassieMaxSeconds)
            throw new ArgumentException($"Announcement lasts {duration:0} s, max is {Ctx.Config.CassieMaxSeconds} s - shorten it");

        string? subtitles = ToolArgs.String(args, "subtitles")?.Trim().Replace("\\n", "\n");
        if (string.IsNullOrEmpty(subtitles))
            subtitles = templateSubtitles;
        if (subtitles != null)
            subtitles = TextSanitizer.SafeRichText(subtitles);
        if (subtitles != null && subtitles.Length > 800)
            throw new ArgumentException("subtitles are longer than 800 characters");

        return new PreparedAction
        {
            Summary = $"{(string.IsNullOrEmpty(templateId) ? "" : $"[{templateId}] ")}\"{Short(TextSanitizer.StripTags(subtitles ?? spoken))}\" ({duration:0.0} s)",
            Extra = new JsonObject
            {
                ["spoken"] = spoken,
                ["duration_sec"] = Math.Round(duration, 1),
                ["fixed_tokens"] = fixes,
            },
            Apply = () =>
            {
                float since = Ctx.Now - Ctx.LastCassieAt;
                if (since < Ctx.Config.CassieMinIntervalSeconds)
                    throw new ArgumentException($"CASSIE spoke {since:0} s ago, minimum interval is {Ctx.Config.CassieMinIntervalSeconds} s - wait");
                Ctx.LastCassieAt = Ctx.Now;
                CassieKit.Send(spoken, subtitles, background);
            },
        };
    }

    private static string Short(string text) => text.Length > 90 ? text.Substring(0, 90) + "…" : text.Replace('\n', ' ');
}
