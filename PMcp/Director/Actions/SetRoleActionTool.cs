using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed class SetRoleActionTool : ActionTool
{
    public static readonly RoleTypeId[] AllowedRoles =
    {
        RoleTypeId.Scp173,
        RoleTypeId.Scp106,
        RoleTypeId.Scp049,
        RoleTypeId.Scp079,
        RoleTypeId.Scp096,
        RoleTypeId.Scp0492,
        RoleTypeId.Scp939,
        RoleTypeId.Scp3114,
        RoleTypeId.ClassD,
        RoleTypeId.Scientist,
        RoleTypeId.FacilityGuard,
        RoleTypeId.NtfPrivate,
        RoleTypeId.NtfSergeant,
        RoleTypeId.NtfSpecialist,
        RoleTypeId.NtfCaptain,
        RoleTypeId.ChaosConscript,
        RoleTypeId.ChaosRifleman,
        RoleTypeId.ChaosMarauder,
        RoleTypeId.ChaosRepressor,
        RoleTypeId.Tutorial,
    };

    public static readonly string[] AllowedRoleNames = AllowedRoles.Select(r => r.ToString()).ToArray();

    public SetRoleActionTool(DirectorContext ctx)
        : base(ctx) { }

    public override string Name => "set_role";
    public override string Title => "Set role";

    public override string Description =>
        "Change a player's role mid-round (vanilla roles). Role changes per round and the SCP count are limited.";

    public override JsonObject InputSchema { get; } =
        Schema.Object(
            new JsonObject
            {
                ["player_id"] = Schema.Int("Player id", 0, int.MaxValue),
                ["role"] = Schema.Str("Role", AllowedRoleNames),
            },
            "player_id",
            "role"
        );

    public static RoleTypeId ParseRole(string? value)
    {
        if (value != null && Enum.TryParse(value.Trim(), true, out RoleTypeId role) && Array.IndexOf(AllowedRoles, role) >= 0)
            return role;
        throw new ArgumentException($"Role '{value}' is not allowed. Allowed: {string.Join(", ", AllowedRoleNames)}");
    }

    public static bool IsScp(RoleTypeId role) => role.ToString().StartsWith("Scp", StringComparison.Ordinal);

    public override PreparedAction Prepare(Dictionary<string, object?> args)
    {
        int id = ToolArgs.Int(args, "player_id", -1, 0, int.MaxValue);
        RoleTypeId role = ParseRole(ToolArgs.String(args, "role"));

        return new PreparedAction
        {
            Summary = $"player {id} -> {role}",
            Apply = () =>
            {
                Player? player = PlayerInfo.RealPlayers().FirstOrDefault(p => p.PlayerId == id);
                if (player == null)
                    throw new ArgumentException($"Player {id} is not on the server");
                if (Ctx.RoleChanges >= Ctx.Config.MaxRoleChangesPerRound)
                    throw new ArgumentException($"Role change limit per round ({Ctx.Config.MaxRoleChangesPerRound}) reached");
                if (IsScp(role))
                {
                    int scps = PlayerInfo.RealPlayers().Count(p => p.PlayerId != id && p.IsAlive && p.IsSCP);
                    int limit = Ctx.ScpLimit(PlayerInfo.RealPlayers().Count);
                    if (scps + 1 > limit)
                        throw new ArgumentException($"That would make more than {limit} SCPs");
                }

                player.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.All);
                Ctx.RoleChanges++;
            },
        };
    }
}
