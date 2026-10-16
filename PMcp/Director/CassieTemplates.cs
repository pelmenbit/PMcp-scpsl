using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MapGeneration;
using PMcp.Tools;

namespace PMcp.Director;

public static class CassieTemplates
{
    private static readonly Regex Placeholder = new(@"\{([a-z_]+)\}");

    public static (string Spoken, string Subtitles) Render(CassieEntry entry, Dictionary<string, object?>? vars)
    {
        var missing = entry.Vars.Where(name => vars == null || !vars.ContainsKey(name) || vars[name] == null).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Template {entry.Id} needs vars: {string.Join(", ", entry.Vars)} (missing: {string.Join(", ", missing)})");

        var spoken = new Dictionary<string, string>();
        var shown = new Dictionary<string, string>();
        foreach (string name in entry.Vars)
        {
            (string s, string t) = Value(name, vars!);
            spoken[name] = s;
            shown[name] = t;
        }

        return (Fill(entry.Spoken, spoken, entry.Id), Fill(entry.Subtitles, shown, entry.Id));
    }

    public static IEnumerable<string> Placeholders(string text) => Placeholder.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value).Distinct();

    private static string Fill(string text, Dictionary<string, string> values, string id) =>
        Placeholder.Replace(
            text,
            m => values.TryGetValue(m.Groups[1].Value, out string v) ? v : throw new ArgumentException($"Template {id}: unknown placeholder {m.Value}")
        );

    private static (string Spoken, string Shown) Value(string name, Dictionary<string, object?> vars)
    {
        switch (name)
        {
            case "scp":
                string digits = new string((ToolArgs.String(vars, name) ?? "").Where(char.IsDigit).ToArray());
                if (digits.Length < 2 || digits.Length > 4)
                    throw new ArgumentException($"vars.scp: SCP number like \"049\" or \"3114\", got '{ToolArgs.String(vars, name)}'");
                return (string.Join(" ", digits.Select(c => c.ToString())), digits);

            case "zone":
                FacilityZone? zone = ActionTool.ParseZone(ToolArgs.String(vars, name));
                if (zone == null)
                    throw new ArgumentException("vars.zone: LightContainment, HeavyContainment, Entrance or Surface");
                return zone.Value switch
                {
                    FacilityZone.LightContainment => ("LIGHT CONTAINMENT ZONE", "Лёгкая зона содержания"),
                    FacilityZone.HeavyContainment => ("HEAVY CONTAINMENT ZONE", "Тяжёлая зона содержания"),
                    FacilityZone.Entrance => ("ENTRANCE ZONE", "Офисная зона"),
                    _ => ("SURFACE", "Поверхность"),
                };

            case "nato":
                string letter = (ToolArgs.String(vars, name) ?? "").Trim().ToUpperInvariant();
                if (letter.StartsWith("NATO_", StringComparison.Ordinal))
                    letter = letter.Substring(5);
                if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z')
                    throw new ArgumentException($"vars.nato: one letter A-Z, got '{letter}'");
                return ("NATO_" + letter, letter);

            default:
                int n = ToolArgs.Int(vars, name, -1, 0, 999);
                return (n.ToString(), n.ToString());
        }
    }
}
