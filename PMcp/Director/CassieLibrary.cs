using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PMcp.Util;

namespace PMcp.Director;

public sealed class CassieEntry
{
    public string Id { get; set; } = "";
    public List<string> Profiles { get; set; } = new();
    public string Situation { get; set; } = "";
    public string Subtitles { get; set; } = "";
    public string Spoken { get; set; } = "";

    public List<string> Vars { get; set; } = new();
}

public sealed class CassieLibrary
{
    public const string FileName = "cassie_library.json";
    private const string ResourceName = "PMcp.cassie_library.json";

    public IReadOnlyList<CassieEntry> Entries { get; }

    private CassieLibrary(List<CassieEntry> entries)
    {
        Entries = entries;
    }

    public static CassieLibrary Load(string directory, Action<string> warn)
    {
        string embedded = ReadEmbedded();
        string path = Path.Combine(directory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, embedded, new UTF8Encoding(false));
            }
            List<CassieEntry> entries = Parse(File.ReadAllText(path, Encoding.UTF8));
            var known = new HashSet<string>(entries.Select(e => e.Id), StringComparer.Ordinal);
            List<CassieEntry> added = Parse(embedded).Where(e => !known.Contains(e.Id)).ToList();
            if (added.Count > 0)
            {
                entries.AddRange(added);
                warn($"{FileName}: из сборки добавлены новые образцы ({added.Count}): {string.Join(", ", added.Select(e => e.Id))}");
            }
            return new CassieLibrary(entries);
        }
        catch (Exception e)
        {
            warn($"{FileName}: {e.Message} - беру встроенную библиотеку");
            return new CassieLibrary(Parse(embedded));
        }
    }

    public string Index(string profile)
    {
        var sb = new StringBuilder();
        foreach (CassieEntry e in Entries)
        {
            if (Fits(e, profile))
                sb.Append(e.Id)
                    .Append(e.Vars.Count > 0 ? " (vars: " + string.Join(", ", e.Vars) + ")" : "")
                    .Append(" - ")
                    .Append(e.Situation)
                    .Append('\n');
        }
        return sb.ToString();
    }

    public CassieEntry? Get(string id) => Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public List<CassieEntry> Find(List<string>? ids, string? query, string profile, int limit)
    {
        var result = new List<CassieEntry>();
        foreach (CassieEntry e in Entries)
        {
            if (result.Count >= limit)
                break;
            bool byId = ids != null && ids.Contains(e.Id);
            bool byQuery =
                !string.IsNullOrWhiteSpace(query)
                && (e.Situation.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || e.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            if ((byId || byQuery) && (byId || Fits(e, profile)))
                result.Add(e);
        }
        return result;
    }

    private static bool Fits(CassieEntry e, string profile) =>
        e.Profiles.Count == 0 || e.Profiles.Exists(p => string.Equals(p, profile, StringComparison.OrdinalIgnoreCase));

    private static string ReadEmbedded()
    {
        using Stream? stream = typeof(CassieLibrary).Assembly.GetManifestResourceStream(ResourceName);
        if (stream == null)
            return "[]";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static List<CassieEntry> Parse(string json)
    {
        var entries = new List<CassieEntry>();
        if (Json.Parse(json) is not List<object?> list)
            throw new FormatException("ожидался массив записей");

        foreach (object? item in list)
        {
            if (item is not Dictionary<string, object?> o)
                continue;
            var entry = new CassieEntry
            {
                Id = o.TryGetValue("id", out object? id) ? id as string ?? "" : "",
                Situation = o.TryGetValue("situation", out object? s) ? s as string ?? "" : "",
                Subtitles = o.TryGetValue("subtitles", out object? sub) ? sub as string ?? "" : "",
                Spoken = o.TryGetValue("spoken", out object? sp) ? sp as string ?? "" : "",
            };
            if (o.TryGetValue("profiles", out object? p) && p is List<object?> profiles)
            {
                foreach (object? profile in profiles)
                {
                    if (profile is string name)
                        entry.Profiles.Add(name);
                }
            }
            entry.Vars.AddRange(CassieTemplates.Placeholders(entry.Spoken + " " + entry.Subtitles));
            if (entry.Id.Length > 0 && entry.Spoken.Length > 0)
                entries.Add(entry);
        }
        return entries;
    }
}
