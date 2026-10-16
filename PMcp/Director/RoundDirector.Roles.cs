using System.Linq;
using LabApi.Events.Arguments.PlayerEvents;
using PlayerRoles;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Director;

public sealed partial class RoundDirector
{
    private void OnChangingRole(PlayerChangingRoleEventArgs ev)
    {
        if (!D.Enabled || !D.AssignRoles || _plan == null || ev.Player == null)
            return;
        if (ev.ChangeReason != RoleChangeReason.RoundStart && ev.ChangeReason != RoleChangeReason.LateJoin)
            return;
        Guard(nameof(OnChangingRole), () => AssignRole(ev));
    }

    private void AssignRole(PlayerChangingRoleEventArgs ev)
    {
        if (ev.NewRole is RoleTypeId.Overwatch or RoleTypeId.Filmmaker or RoleTypeId.Tutorial or RoleTypeId.None)
            return;
        if (!PlayerInfo.IsReal(ev.Player) || _plan == null)
            return;

        int id = ev.Player.PlayerId;
        string source;
        if (_plan.Roles.TryGetValue(id, out RoleTypeId role) && ev.ChangeReason == RoleChangeReason.RoundStart)
            source = "план";
        else if (_assigned.TryGetValue(id, out role))
            source = "доля, повторно";
        else if (_plan.RoleMix.Count > 0)
        {
            role = RoleMixer.Pick(_plan.RoleMix, _assigned.Values);
            source = ev.ChangeReason == RoleChangeReason.LateJoin ? "опоздал, доля" : "нет в плане, доля";
        }
        else
            return;

        _assigned[id] = role;
        string who = $"{TextSanitizer.Clean(ev.Player.Nickname, 32)} ({id})";
        if (D.DryRun)
        {
            _ctx.Log($"[dry] роль ({source}): {who} {ev.NewRole} -> {role}");
            return;
        }
        if (source != "план")
            _ctx.Log($"роль ({source}): {who} -> {role}, выдано: {MixCounts()}");
        ev.NewRole = role;
        ev.SpawnFlags = RoleSpawnFlags.All;
    }

    private string MixCounts() =>
        string.Join(", ", _assigned.Values.GroupBy(r => r).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
}
