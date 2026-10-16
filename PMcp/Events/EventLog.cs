using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.Scp079Events;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Arguments.WarheadEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Events;

public sealed class EventLog
{
    public static readonly string[] Types =
    {
        "round_started",
        "round_ended",
        "wave_respawned",
        "joined",
        "left",
        "death",
        "escaped",
        "banned",
        "kicked",
        "report",
        "cheater_report",
        "generator_activated",
        "decontamination_started",
        "warhead_started",
        "warhead_stopped",
        "warhead_detonated",
        "scp079_recontained",
        "cuffed",
    };

    private readonly JsonObject[] _buffer;
    private readonly bool _exposeUserIds;
    private int _head;
    private int _count;

    public long LatestSeq { get; private set; }
    public int CurrentRound { get; private set; }
    public int RoundDeaths { get; private set; }
    public int RoundEscapes { get; private set; }

    public EventLog(int capacity, bool exposeUserIds)
    {
        _buffer = new JsonObject[Math.Max(10, capacity)];
        _exposeUserIds = exposeUserIds;
    }

    public void Subscribe()
    {
        ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
        ServerEvents.RoundStarted += OnRoundStarted;
        ServerEvents.RoundEnded += OnRoundEnded;
        ServerEvents.WaveRespawned += OnWaveRespawned;
        PlayerEvents.Joined += OnJoined;
        PlayerEvents.Left += OnLeft;
        PlayerEvents.Death += OnDeath;
        PlayerEvents.Escaped += OnEscaped;
        PlayerEvents.Banned += OnBanned;
        PlayerEvents.Kicked += OnKicked;
        PlayerEvents.ReportedPlayer += OnReportedPlayer;
        PlayerEvents.ReportedCheater += OnReportedCheater;
        PlayerEvents.Cuffed += OnCuffed;
        ServerEvents.GeneratorActivated += OnGeneratorActivated;
        ServerEvents.LczDecontaminationStarted += OnDecontaminationStarted;
        WarheadEvents.Started += OnWarheadStarted;
        WarheadEvents.Stopped += OnWarheadStopped;
        WarheadEvents.Detonated += OnWarheadDetonated;
        Scp079Events.Recontained += OnScp079Recontained;
    }

    public void Unsubscribe()
    {
        ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
        ServerEvents.RoundStarted -= OnRoundStarted;
        ServerEvents.RoundEnded -= OnRoundEnded;
        ServerEvents.WaveRespawned -= OnWaveRespawned;
        PlayerEvents.Joined -= OnJoined;
        PlayerEvents.Left -= OnLeft;
        PlayerEvents.Death -= OnDeath;
        PlayerEvents.Escaped -= OnEscaped;
        PlayerEvents.Banned -= OnBanned;
        PlayerEvents.Kicked -= OnKicked;
        PlayerEvents.ReportedPlayer -= OnReportedPlayer;
        PlayerEvents.ReportedCheater -= OnReportedCheater;
        PlayerEvents.Cuffed -= OnCuffed;
        ServerEvents.GeneratorActivated -= OnGeneratorActivated;
        ServerEvents.LczDecontaminationStarted -= OnDecontaminationStarted;
        WarheadEvents.Started -= OnWarheadStarted;
        WarheadEvents.Stopped -= OnWarheadStopped;
        WarheadEvents.Detonated -= OnWarheadDetonated;
        Scp079Events.Recontained -= OnScp079Recontained;
    }

    public List<JsonObject> Snapshot(long sinceSeq, int limit, List<string>? types, out bool hasMore)
    {
        var matched = new List<JsonObject>();
        int start = (_head - _count + _buffer.Length) % _buffer.Length;
        for (int i = 0; i < _count; i++)
        {
            JsonObject e = _buffer[(start + i) % _buffer.Length];
            if ((long)e["seq"]! <= sinceSeq)
                continue;
            if (types != null && !types.Contains((string)e["type"]!))
                continue;
            matched.Add(e);
        }

        hasMore = matched.Count > limit;
        if (!hasMore)
            return matched;
        return sinceSeq > 0 ? matched.GetRange(0, limit) : matched.GetRange(matched.Count - limit, limit);
    }

    private void Add(string type, JsonObject data)
    {
        var e = new JsonObject
        {
            ["seq"] = ++LatestSeq,
            ["type"] = type,
            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["round"] = CurrentRound,
        };
        if (Round.IsRoundStarted)
            e["round_sec"] = (long)Round.Duration.TotalSeconds;
        foreach (KeyValuePair<string, object?> pair in data)
            e[pair.Key] = pair.Value;

        _buffer[_head] = e;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length)
            _count++;
    }

    private static void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Logger.Warn($"EventLog.{what}: {e.Message}");
        }
    }

    private static JsonObject? Who(Player? p) => PlayerInfo.Ref(p);

    private static string? Text(string? value, int max) => TextSanitizer.Clean(value, max);

    private void OnWaitingForPlayers() => Safe(nameof(OnWaitingForPlayers), () =>
    {
        CurrentRound++;
        RoundDeaths = 0;
        RoundEscapes = 0;
    });

    private void OnRoundStarted() => Safe(nameof(OnRoundStarted), () => Add("round_started", new JsonObject
    {
        ["players"] = PlayerInfo.RealPlayers().Count,
    }));

    private void OnRoundEnded(RoundEndedEventArgs ev) => Safe(nameof(OnRoundEnded), () => Add("round_ended", new JsonObject
    {
        ["leading_team"] = ev.LeadingTeam.ToString(),
    }));

    private void OnWaveRespawned(WaveRespawnedEventArgs ev) => Safe(nameof(OnWaveRespawned), () =>
    {
        var spawned = new List<object?>();
        if (ev.Players != null)
        {
            foreach (Player p in ev.Players)
                spawned.Add(Who(p));
        }
        Add("wave_respawned", new JsonObject
        {
            ["faction"] = ev.Wave?.Faction.ToString(),
            ["players"] = spawned,
        });
    });

    private void OnJoined(PlayerJoinedEventArgs ev) => Safe(nameof(OnJoined), () =>
    {
        if (!PlayerInfo.IsReal(ev.Player))
            return;
        JsonObject? who = Who(ev.Player);
        if (who != null && _exposeUserIds)
            who["user_id"] = ev.Player.UserId;
        Add("joined", new JsonObject { ["player"] = who });
    });

    private void OnLeft(PlayerLeftEventArgs ev) => Safe(nameof(OnLeft), () =>
    {
        Player p = ev.Player;
        if (p == null || !PlayerInfo.IsCounted(p))
            return;

        string? role;
        try
        {
            role = p.Role.ToString();
        }
        catch
        {
            role = null;
        }

        Add("left", new JsonObject
        {
            ["player"] = Who(p),
            ["role"] = role,
        });
    });

    private void OnDeath(PlayerDeathEventArgs ev) => Safe(nameof(OnDeath), () =>
    {
        if (ev.Player == null || ev.Player.IsHost)
            return;
        if (PlayerInfo.IsCounted(ev.Player))
            RoundDeaths++;
        Add("death", new JsonObject
        {
            ["player"] = Who(ev.Player),
            ["role"] = ev.OldRole.ToString(),
            ["team"] = PlayerRoles.PlayerRolesUtils.GetTeam(ev.OldRole).ToString(),
            ["attacker"] = Who(ev.Attacker),
            ["attacker_role"] = ev.Attacker != null && !ev.Attacker.IsHost ? ev.Attacker.Role.ToString() : null,
            ["reason"] = Text(ev.DamageHandler?.ServerLogsText, 200),
        });
    });

    private void OnEscaped(PlayerEscapedEventArgs ev) => Safe(nameof(OnEscaped), () =>
    {
        RoundEscapes++;
        Add("escaped", new JsonObject
        {
            ["player"] = Who(ev.Player),
            ["old_role"] = ev.OldRole.ToString(),
            ["new_role"] = ev.NewRole.ToString(),
            ["scenario"] = ev.EscapeScenarioType.ToString(),
        });
    });

    private void OnBanned(PlayerBannedEventArgs ev) => Safe(nameof(OnBanned), () =>
    {
        var data = new JsonObject
        {
            ["player"] = Who(ev.Player),
            ["issuer"] = Who(ev.Issuer),
            ["reason"] = Text(ev.Reason, 256),
            ["duration_sec"] = ev.Duration,
        };
        if (ev.Player == null && _exposeUserIds && ev.PlayerId != null && ev.PlayerId.IndexOf('@') >= 0)
            data["user_id"] = ev.PlayerId;
        Add("banned", data);
    });

    private void OnKicked(PlayerKickedEventArgs ev) => Safe(nameof(OnKicked), () => Add("kicked", new JsonObject
    {
        ["player"] = Who(ev.Player),
        ["issuer"] = Who(ev.Issuer),
        ["reason"] = Text(ev.Reason, 256),
    }));

    private void OnReportedPlayer(PlayerReportedPlayerEventArgs ev) => Safe(nameof(OnReportedPlayer), () => Add("report", new JsonObject
    {
        ["reporter"] = Who(ev.Player),
        ["target"] = Who(ev.Target),
        ["reason"] = Text(ev.Reason, 256),
    }));

    private void OnReportedCheater(PlayerReportedCheaterEventArgs ev) => Safe(nameof(OnReportedCheater), () => Add("cheater_report", new JsonObject
    {
        ["reporter"] = Who(ev.Player),
        ["target"] = Who(ev.Target),
        ["reason"] = Text(ev.Reason, 256),
    }));

    private void OnCuffed(PlayerCuffedEventArgs ev) => Safe(nameof(OnCuffed), () =>
    {
        if (!PlayerInfo.IsReal(ev.Target))
            return;
        Add("cuffed", new JsonObject
        {
            ["player"] = Who(ev.Target),
            ["role"] = ev.Target.Role.ToString(),
            ["by"] = Who(ev.Player),
            ["by_role"] = ev.Player?.Role.ToString(),
        });
    });

    private void OnGeneratorActivated(GeneratorActivatedEventArgs ev) => Safe(nameof(OnGeneratorActivated), () =>
    {
        int engaged = 0;
        int total = 0;
        foreach (Generator generator in Generator.List)
        {
            total++;
            if (generator.Engaged)
                engaged++;
        }
        Add("generator_activated", new JsonObject
        {
            ["room"] = ev.Generator?.Room?.Name.ToString(),
            ["engaged"] = engaged,
            ["total"] = total,
        });
    });

    private void OnDecontaminationStarted() => Safe(nameof(OnDecontaminationStarted), () => Add("decontamination_started", new JsonObject()));

    private void OnWarheadStarted(WarheadStartedEventArgs ev) => Safe(nameof(OnWarheadStarted), () => Add("warhead_started", new JsonObject
    {
        ["by"] = Who(ev.Player),
        ["automatic"] = ev.IsAutomatic,
    }));

    private void OnWarheadStopped(WarheadStoppedEventArgs ev) => Safe(nameof(OnWarheadStopped), () => Add("warhead_stopped", new JsonObject
    {
        ["by"] = Who(ev.Player),
    }));

    private void OnWarheadDetonated(WarheadDetonatedEventArgs ev) => Safe(nameof(OnWarheadDetonated), () => Add("warhead_detonated", new JsonObject()));

    private void OnScp079Recontained(Scp079RecontainedEventArgs ev) => Safe(nameof(OnScp079Recontained), () => Add("scp079_recontained", new JsonObject
    {
        ["player"] = Who(ev.Player),
        ["by"] = Who(ev.Activator),
    }));
}