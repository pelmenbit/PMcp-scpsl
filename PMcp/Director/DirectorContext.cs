using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using MEC;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace PMcp.Director;

public sealed class DirectorAction
{
    public long RoundSec { get; set; }
    public string Source { get; set; } = "";
    public string Action { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Outcome { get; set; } = "";
}

public sealed class PlanStats
{
    public int Attempts { get; set; }
    public int Dropped { get; set; }
    public int Fixed { get; set; }

    public void Reset()
    {
        Attempts = 0;
        Dropped = 0;
        Fixed = 0;
    }
}

public sealed class DirectorContext
{
    private const int RecentLimit = 20;

    private readonly Config _config;
    private readonly List<CoroutineHandle> _scheduled = new();
    private readonly List<DirectorAction> _recent = new();

    public DirectorContext(Config config)
    {
        _config = config;
    }

    public DirectorConfig Config => _config.Director;
    public bool DryRun => _config.Director.DryRun;
    public bool ExposeUserIds => _config.ExposeUserIds;

    public int RoleChanges { get; set; }
    public float LastCassieAt { get; set; } = -1000f;
    public float TeslasDisabledUntil { get; set; }

    public Action<RoundPlan>? PlanAccepted { get; set; }
    public Action<ReplanTail>? TailAccepted { get; set; }

    public Dictionary<int, string> PlanPlayers { get; } = new();
    public HashSet<int> RecentScps { get; } = new();
    public bool PlanningAhead { get; set; }
    public bool PlanningBench { get; set; }
    public PlanStats PlanStats { get; } = new();
    public string ActionSource { get; set; } = "ai";

    public IReadOnlyList<DirectorAction> RecentActions => _recent;

    public float Now => Time.realtimeSinceStartup;

    public long RoundSec => Round.IsRoundStarted ? (long)Round.Duration.TotalSeconds : 0;

    public bool PlanForStart => PlanningAhead || PlanningBench || !Round.IsRoundStarted;

    public void Start()
    {
        ServerEvents.WaitingForPlayers += ResetRound;
        PlayerEvents.TriggeringTesla += OnTriggeringTesla;
    }

    public void Stop()
    {
        ServerEvents.WaitingForPlayers -= ResetRound;
        PlayerEvents.TriggeringTesla -= OnTriggeringTesla;
        ResetRound();
    }

    public bool IsAllowed(string action) => Config.AllowedActions.Exists(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase));

    public int ScpLimit(int players)
    {
        int limit = Config.MaxScps;
        if (Config.ScpsByPlayers.Count == 0)
            return limit;
        List<KeyValuePair<int, int>> rows = Config.ScpsByPlayers.OrderBy(p => p.Key).ToList();
        int byTable = rows[rows.Count - 1].Value;
        foreach (KeyValuePair<int, int> row in rows)
        {
            if (players <= row.Key)
            {
                byTable = row.Value;
                break;
            }
        }
        return Math.Max(0, Math.Min(limit, byTable));
    }

    public void Log(string message) => Logger.Info("Дирижёр: " + message);

    public void RecordAction(string action, string summary, string outcome)
    {
        _recent.Add(new DirectorAction
        {
            RoundSec = RoundSec,
            Source = ActionSource,
            Action = action,
            Summary = summary,
            Outcome = outcome,
        });
        if (_recent.Count > RecentLimit)
            _recent.RemoveAt(0);
    }

    public void Schedule(float seconds, string what, Action action)
    {
        _scheduled.Add(
            Timing.CallDelayed(
                Math.Max(0f, seconds),
                () =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        Logger.Warn($"Дирижёр: отложенное «{what}» упало: {e.Message}");
                    }
                }
            )
        );
    }

    public void ResetRound()
    {
        foreach (CoroutineHandle handle in _scheduled)
        {
            if (handle.IsRunning)
                Timing.KillCoroutines(handle);
        }
        _scheduled.Clear();
        _recent.Clear();
        RoleChanges = 0;
        LastCassieAt = -1000f;
        TeslasDisabledUntil = 0f;
    }

    private void OnTriggeringTesla(PlayerTriggeringTeslaEventArgs ev)
    {
        if (TeslasDisabledUntil > 0f && Now < TeslasDisabledUntil)
            ev.IsAllowed = false;
    }
}
