using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LabApi.Features.Console;
using PMcp.Util;

namespace PMcp.Ai;

public sealed class TokenLedger
{
    private readonly Config _config;
    private readonly string _path;
    private readonly string[] _legacyPaths;
    private DateTime _day = DateTime.UtcNow.Date;
    private long _today;
    private bool _warned;

    public TokenLedger(Config config, string directory)
    {
        _config = config;
        _path = Path.Combine(directory, "usage.json");
        _legacyPaths = new[] { Path.Combine(directory, "director_state.json"), Path.Combine(directory, "director_usage.json") };
        Load();
    }

    public long Today
    {
        get
        {
            Roll();
            return _today;
        }
    }

    public int Budget => _config.Director.DailyTokenBudget;

    public bool Allow(out string message)
    {
        Roll();
        if (Budget > 0 && _today >= Budget)
        {
            message = $"дневной лимит токенов {Budget} исчерпан ({_today})";
            if (!_warned)
                Logger.Warn("PMcp: " + message + " - вызовы модели остановлены до конца суток UTC");
            _warned = true;
            return false;
        }
        message = "";
        return true;
    }

    public long Cost(AgentResult result) => Cost(result, _config.Ai.CachedTokenWeight);

    public static long Cost(AgentResult result, double cachedWeight)
    {
        long cached = Math.Min(result.CachedTokens, result.PromptTokens);
        double weight = Math.Max(0, Math.Min(1, cachedWeight));
        return result.PromptTokens - cached + (long)Math.Round(cached * weight) + result.CompletionTokens;
    }

    public void Add(AgentResult result)
    {
        Roll();
        _today += Cost(result);
        Save();
    }

    private void Roll()
    {
        DateTime today = DateTime.UtcNow.Date;
        if (today == _day)
            return;
        _day = today;
        _today = 0;
        _warned = false;
    }

    private void Load()
    {
        foreach (string path in Candidates())
        {
            try
            {
                if (!File.Exists(path) || Json.Parse(File.ReadAllText(path, Encoding.UTF8)) is not Dictionary<string, object?> o)
                    continue;
                if (o.TryGetValue("day", out object? day) && day as string == DateTime.UtcNow.Date.ToString("yyyy-MM-dd")
                    && o.TryGetValue("tokens", out object? t) && t is double tokens)
                    _today = (long)tokens;
                return;
            }
            catch (Exception e)
            {
                Logger.Warn($"PMcp: не прочитал {path}: {e.Message}");
            }
        }
    }

    private IEnumerable<string> Candidates()
    {
        yield return _path;
        foreach (string legacy in _legacyPaths)
            yield return legacy;
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, Json.Write(new JsonObject { ["day"] = _day.ToString("yyyy-MM-dd"), ["tokens"] = _today }), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Logger.Warn($"PMcp: не записал {_path}: {e.Message}");
        }
    }
}
