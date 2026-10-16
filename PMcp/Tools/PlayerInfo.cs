using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using PMcp.Util;
using UnityEngine;

namespace PMcp.Tools;

internal static class PlayerInfo
{
    public const int NickMax = 64;

    public static bool CountDummies { get; set; }

    public static bool IsReal(Player? p) => p != null && !p.IsDestroyed && IsCounted(p);

    public static bool IsCounted(Player p) => !p.IsHost && (p.IsDummy ? CountDummies : !p.IsNpc);

    public static List<Player> RealPlayers()
    {
        var result = new List<Player>();
        foreach (Player p in Player.List)
        {
            if (IsReal(p))
                result.Add(p);
        }
        result.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
        return result;
    }

    public static JsonObject? Ref(Player? p)
    {
        if (p == null)
            return null;
        if (p.IsHost)
            return new JsonObject { ["server"] = true };
        return new JsonObject
        {
            ["id"] = p.PlayerId,
            ["nick"] = TextSanitizer.Clean(p.Nickname, NickMax),
        };
    }

    public static JsonObject Brief(Player p, bool exposeUserIds)
    {
        var o = new JsonObject
        {
            ["id"] = p.PlayerId,
            ["nick"] = TextSanitizer.Clean(p.Nickname, NickMax),
        };

        string? display = SafeDisplayName(p);
        if (!string.IsNullOrEmpty(display) && display != p.Nickname)
            o["display_name"] = TextSanitizer.Clean(display, NickMax);
        if (exposeUserIds)
            o["user_id"] = p.UserId;

        o["role"] = p.Role.ToString();
        o["team"] = p.Team.ToString();
        o["alive"] = p.IsAlive;

        if (p.IsAlive)
        {
            o["hp"] = Round1(p.Health);
            o["max_hp"] = Round1(p.MaxHealth);
            if (p.ArtificialHealth > 0)
                o["ahp"] = Round1(p.ArtificialHealth);
            if (p.HumeShield > 0)
                o["hume_shield"] = Round1(p.HumeShield);
            o["zone"] = p.Zone.ToString();
            Room? room = p.Room;
            if (room != null)
                o["room"] = room.Name.ToString();
        }

        string group = p.GroupName;
        if (!string.IsNullOrEmpty(group))
            o["group"] = TextSanitizer.Clean(group, 32);

        List<string> flags = Flags(p);
        if (flags.Count > 0)
            o["flags"] = flags;
        return o;
    }

    public static JsonObject Full(Player p, bool exposeUserIds)
    {
        JsonObject o = Brief(p, exposeUserIds);

        if (p.IsAlive)
        {
            Vector3 pos = p.Position;
            o["position"] = new[] { Round1(pos.x), Round1(pos.y), Round1(pos.z) };
            o["stamina"] = Round1(p.StaminaRemaining * 100f);

            Item? current = p.CurrentItem;
            o["current_item"] = current?.Type.ToString();

            var items = new List<object>();
            foreach (Item item in p.Items)
            {
                if (item == null)
                    continue;
                items.Add(new JsonObject { ["type"] = item.Type.ToString(), ["serial"] = (int)item.Serial });
            }
            o["items"] = items;

            var ammo = new JsonObject();
            foreach (KeyValuePair<ItemType, ushort> pair in p.Ammo)
            {
                if (pair.Value > 0)
                    ammo[pair.Key.ToString()] = (int)pair.Value;
            }
            o["ammo"] = ammo;

            var effects = new List<object>();
            foreach (var effect in p.ActiveEffects)
            {
                if (effect == null)
                    continue;
                effects.Add(
                    new JsonObject
                    {
                        ["name"] = effect.GetType().Name,
                        ["intensity"] = (int)effect.Intensity,
                        ["time_left_sec"] = effect.Duration <= 0f ? null : Round1(effect.TimeLeft),
                    }
                );
            }
            o["effects"] = effects;

            if (p.IsDisarmed)
                o["disarmed_by"] = Ref(p.DisarmedBy);
        }
        else
        {
            Player? target = p.CurrentlySpectating;
            if (target != null && !target.IsHost)
                o["spectating"] = Ref(target);
        }

        o["spectators"] = p.CurrentSpectators?.Count ?? 0;

        string? info = TextSanitizer.Clean(p.CustomInfo, 128);
        if (!string.IsNullOrEmpty(info))
            o["custom_info"] = info;
        return o;
    }

    public static double Round1(float value) => Math.Round(value, 1);

    private static string? SafeDisplayName(Player p)
    {
        try
        {
            return p.DisplayName;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> Flags(Player p)
    {
        var flags = new List<string>(4);
        if (p.IsGodModeEnabled)
            flags.Add("god");
        if (p.IsNoclipEnabled)
            flags.Add("noclip");
        if (p.IsBypassEnabled)
            flags.Add("bypass");
        if (p.IsOverwatchEnabled)
            flags.Add("overwatch");
        if (p.IsMuted)
            flags.Add("muted");
        if (p.IsDisarmed)
            flags.Add("disarmed");
        if (p.RemoteAdminAccess)
            flags.Add("remote_admin");
        return flags;
    }
}
