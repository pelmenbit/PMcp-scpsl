using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LabApi.Features.Wrappers;
using CassieLine = global::Cassie.CassieLine;
using CassieClipCategory = global::Cassie.CassieClipCategory;
using CassiePlaybackModifiers = global::Cassie.CassiePlaybackModifiers;
using CassieTtsPayload = global::Cassie.CassieTtsPayload;

namespace PMcp.Director;

public static class CassieKit
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex Tags = new(@"<[^<>]*>");

    private static readonly (string Prefix, int Parts)[] Modifiers =
    {
        ("PITCH", 1),
        ("VOL", 1),
        ("STARTT", 1),
        ("MAXDUR", 1),
        ("SLEEP", 1),
        ("SPAC", 1),
        ("STUTT", 3),
    };

    private static readonly Regex LegacyPitch = new(@"^\$?PI?TCH_([\d.]+)-?$");
    private static readonly Regex LegacyYd = new(@"^\$?YD_([\d.]+)$");
    private static readonly Regex LegacyYield = new(@"^\$?YIELD_([\d.]+)$");
    private static readonly Regex LegacyJam = new(@"^\$?JAM_(\d+)_(\d+)$");
    private static readonly Regex Glitch = new(@"^\.G(\d+)(\.*)$");

    private static string? _vocabularyText;
    private static readonly Dictionary<string, bool> Known = new(StringComparer.Ordinal);
    private const int MaxSuggestions = 8;

    public static bool Available => Announcer.LineDatabase != null;

    public static string Normalize(string? raw, List<string> fixes)
    {
        string text = Tags.Replace(raw ?? "", " ");
        var sb = new StringBuilder(text.Length);
        foreach (string part in text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string token in Modernize(part.ToUpperInvariant(), fixes))
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(token);
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<string> Modernize(string token, List<string> fixes)
    {
        Match m;
        if (!token.StartsWith("$", StringComparison.Ordinal) && (m = LegacyPitch.Match(token)).Success)
            return Fixed(token, $"$PITCH_{Num(Clamp(Parse(m.Groups[1].Value, 1), 0.01, 15))}", fixes);
        if ((m = LegacyYd.Match(token)).Success)
            return Fixed(token, $"$SPAC_{Num(Parse(m.Groups[1].Value, 0.1))}", fixes);
        if ((m = LegacyYield.Match(token)).Success)
            return Fixed(token, $"$SLEEP_{Num(Parse(m.Groups[1].Value, 0.5))}", fixes);
        if ((m = LegacyJam.Match(token)).Success)
        {
            double start = Math.Min(1, int.Parse(m.Groups[1].Value, Inv) / 100.0);
            return Fixed(token, $"$STUTT_{Num(start)}_0.08_{int.Parse(m.Groups[2].Value, Inv)}", fixes);
        }
        if ((m = Glitch.Match(token)).Success)
        {
            int n = Math.Min(7, Math.Max(1, int.Parse(m.Groups[1].Value, Inv)));
            string glitch = ".G" + n;
            if (m.Groups[2].Length == 0 && glitch == token)
                return new[] { token };
            fixes.Add($"{token}->{glitch}");
            return m.Groups[2].Length > 0 ? new[] { glitch, "." } : new[] { glitch };
        }
        return new[] { token };
    }

    private static IEnumerable<string> Fixed(string from, string to, List<string> fixes)
    {
        fixes.Add($"{from}->{to}");
        return new[] { to };
    }

    public static bool IsModifier(string token)
    {
        if (token.Length < 2 || token[0] != '$')
            return false;

        foreach ((string prefix, int parts) in Modifiers)
        {
            if (token.Length <= prefix.Length || string.CompareOrdinal(token, 1, prefix, 0, prefix.Length) != 0)
                continue;

            int numbers = 0;
            string[] pieces = token.Split('_');
            for (int i = 1; i < pieces.Length; i++)
            {
                if (float.TryParse(pieces[i], NumberStyles.Float, Inv, out _))
                    numbers++;
            }
            return numbers >= parts;
        }
        return false;
    }

    public static List<string> UnknownTokens(string normalized)
    {
        var unknown = new List<string>();
        if (!Available)
            return unknown;

        foreach (string token in normalized.Split(' '))
        {
            if (token.Length == 0 || IsModifier(token))
                continue;
            if (!IsKnown(token))
            {
                if (!unknown.Contains(token))
                    unknown.Add(token);
            }
        }
        return unknown;
    }

    private static bool IsKnown(string token)
    {
        if (token[0] == '$')
            return false;
        if (!Known.TryGetValue(token, out bool known))
        {
            known = Announcer.CalculateDuration(token, default(CassiePlaybackModifiers)) > 0;
            Known[token] = known;
        }
        return known;
    }

    public static string DescribeUnknown(List<string> unknown)
    {
        List<string> words = Words();
        var parts = new List<string>(unknown.Count);
        foreach (string token in unknown)
        {
            if (parts.Count >= MaxSuggestions)
            {
                parts.Add(token);
                continue;
            }
            string bare = token.TrimEnd('.', ',');
            var similar = words
                .Select(w => (Word: w, Score: Similarity(bare, w)))
                .Where(x => x.Score < 3)
                .OrderBy(x => x.Score)
                .ThenBy(x => x.Word.Length)
                .Take(3)
                .Select(x => x.Word)
                .ToList();
            parts.Add(similar.Count > 0 ? $"{token} (maybe: {string.Join(", ", similar)})" : token);
        }
        return string.Join(", ", parts);
    }

    private static int Similarity(string token, string word)
    {
        if (token.Length >= 3 && word.Length >= 3 && (word.StartsWith(token, StringComparison.Ordinal) || token.StartsWith(word, StringComparison.Ordinal)))
            return 0;
        if (token.Length < 4 || Math.Abs(token.Length - word.Length) > 2)
            return 3;

        var prev = new int[word.Length + 1];
        var cur = new int[word.Length + 1];
        for (int j = 0; j <= word.Length; j++)
            prev[j] = j;
        for (int i = 1; i <= token.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= word.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (token[i - 1] == word[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[word.Length];
    }

    private static List<string>? _words;

    private static List<string> Words()
    {
        if (_words != null)
            return _words;
        _words = Announcer.AllLines
            .Where(l => l.Type == CassieClipCategory.Word && !string.IsNullOrEmpty(l.ApiName) && !l.ApiName.StartsWith("_", StringComparison.Ordinal) && l.ApiName != ".")
            .Select(l => l.ApiName)
            .Distinct()
            .ToList();
        return _words;
    }

    public static double Duration(string normalized, bool background) =>
        Announcer.CalculateDuration(normalized, default(CassiePlaybackModifiers)) + (background ? 2.5 : 0);

    public static void Send(string normalized, string? subtitles, bool background)
    {
        CassieTtsPayload payload = string.IsNullOrWhiteSpace(subtitles)
            ? new CassieTtsPayload(normalized, true, background)
            : new CassieTtsPayload(normalized, subtitles, background);
        Announcer.Message(payload, 0f, 0f);
    }

    public static string VocabularyText()
    {
        if (_vocabularyText != null)
            return _vocabularyText;

        CassieLine[] lines = Announcer.AllLines;
        if (lines.Length == 0)
            return "(vocabulary unavailable - CASSIE line database is not loaded yet)";

        var words = new SortedSet<string>(StringComparer.Ordinal);
        var sounds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (CassieLine line in lines)
        {
            string name = line.ApiName;
            if (string.IsNullOrEmpty(name) || name.StartsWith("_", StringComparison.Ordinal) || name.EndsWith("-", StringComparison.Ordinal) || name.StartsWith("-", StringComparison.Ordinal))
                continue;
            if (line.Type == CassieClipCategory.Word && name != ".")
                words.Add(name);
            else
                sounds.Add(name);
        }

        _vocabularyText = $"WORDS ({words.Count}): {string.Join(" ", words)}\nSOUNDS: {string.Join(" ", sounds.Where(s => s != "."))}";
        return _vocabularyText;
    }

    private static double Parse(string value, double fallback) =>
        double.TryParse(value.TrimEnd('.', '-'), NumberStyles.Float, Inv, out double v) ? v : fallback;

    private static double Clamp(double v, double min, double max) => Math.Min(max, Math.Max(min, v));

    private static string Num(double v) => v.ToString("0.###", Inv);
}
