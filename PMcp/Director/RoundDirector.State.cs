using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using MapGeneration;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed partial class RoundDirector
{
    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath) || Json.Parse(File.ReadAllText(_statePath, Encoding.UTF8)) is not Dictionary<string, object?> o)
                return;
            if (o.TryGetValue("themes", out object? th) && th is List<object?> themes)
                _themes.AddRange(themes.OfType<string>());
        }
        catch (Exception e)
        {
            Logger.Warn($"Дирижёр: не прочитал {_statePath}: {e.Message} - темы прошлых раундов с нуля");
        }
    }

    private void SaveState()
    {
        try
        {
            File.WriteAllText(_statePath, Json.Write(new JsonObject { ["themes"] = _themes }), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Logger.Warn($"Дирижёр: не записал {_statePath}: {e.Message}");
        }
    }

    private void CheckLibrary()
    {
        if (_libraryChecked || !CassieKit.Available)
            return;
        _libraryChecked = true;

        var sample = new Dictionary<string, object?> { ["scp"] = "049", ["zone"] = "LightContainment", ["nato"] = "E" };
        var problems = new List<string>();
        foreach (CassieEntry entry in _library.Entries)
        {
            try
            {
                var vars = new Dictionary<string, object?>(sample);
                foreach (string name in entry.Vars)
                {
                    if (!vars.ContainsKey(name))
                        vars[name] = 5.0;
                }
                string spoken = CassieKit.Normalize(CassieTemplates.Render(entry, vars).Spoken, new List<string>());
                List<string> unknown = CassieKit.UnknownTokens(spoken);
                if (unknown.Count > 0)
                    problems.Add($"{entry.Id}: {string.Join(" ", unknown)}");
            }
            catch (ArgumentException e)
            {
                problems.Add($"{entry.Id}: {e.Message}");
            }
        }
        if (problems.Count > 0)
            Logger.Warn($"Дирижёр: в {CassieLibrary.FileName} слова, которых CASSIE не скажет:\n  " + string.Join("\n  ", problems));
    }

    private static object ActionJson(DirectorAction a) =>
        new JsonObject
        {
            ["round_sec"] = a.RoundSec,
            ["by"] = a.Source,
            ["action"] = a.Action,
            ["summary"] = TextSanitizer.StripTags(a.Summary),
            ["outcome"] = a.Outcome,
        };

    private static object EntryJson(TimelineEntry e)
    {
        var o = new JsonObject { ["at_sec"] = e.AtSec, ["action"] = e.Action, ["args"] = e.Args };
        if (e.When != null)
            o["when"] = e.When;
        return o;
    }

    private static JsonObject StateJson()
    {
        List<Player> alive = PlayerInfo.RealPlayers().Where(p => p.IsAlive).ToList();
        var zones = new JsonObject();
        foreach (IGrouping<FacilityZone, Player> group in alive.GroupBy(p => p.Zone))
            zones[group.Key.ToString()] = group.Count();

        var state = new JsonObject
        {
            ["alive_by_team"] = ToJson(AliveByTeam()),
            ["scps_alive"] = alive.Where(p => p.IsSCP).Select(p => p.Role.ToString()).ToList(),
            ["players_by_zone"] = zones,
        };
        try
        {
            state["warhead"] = Warhead.IsDetonated ? "detonated" : Warhead.IsDetonationInProgress ? "in_progress" : "idle";
            state["decontamination"] = Decontamination.Status.ToString();
            state["generators_engaged"] = Generator.List.Count(g => g.Engaged);
        }
        catch (Exception e)
        {
            state["error"] = e.Message;
        }
        return state;
    }

    private static Dictionary<string, int> AliveByTeam()
    {
        var alive = new Dictionary<string, int>();
        foreach (Player p in PlayerInfo.RealPlayers())
        {
            if (!p.IsAlive)
                continue;
            string team = p.Team.ToString();
            alive[team] = alive.TryGetValue(team, out int n) ? n + 1 : 1;
        }
        return alive;
    }

    private static JsonObject ToJson(Dictionary<string, int> map)
    {
        var o = new JsonObject();
        foreach (KeyValuePair<string, int> pair in map.OrderByDescending(p => p.Value))
            o[pair.Key] = pair.Value;
        return o;
    }
}
