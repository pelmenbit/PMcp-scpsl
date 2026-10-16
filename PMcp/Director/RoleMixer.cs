using System.Collections.Generic;
using System.Linq;
using PlayerRoles;

namespace PMcp.Director;

public static class RoleMixer
{
    public static RoleTypeId Pick(IReadOnlyDictionary<RoleTypeId, int> mix, IEnumerable<RoleTypeId> assigned)
    {
        var counts = new Dictionary<RoleTypeId, int>();
        foreach (RoleTypeId given in assigned)
            counts[given] = counts.TryGetValue(given, out int n) ? n + 1 : 1;
        int total = mix.Keys.Sum(r => counts.TryGetValue(r, out int c) ? c : 0) + 1;
        double weights = mix.Values.Sum();
        RoleTypeId best = mix.Keys.First();
        double bestGap = double.MinValue;
        foreach (KeyValuePair<RoleTypeId, int> pair in mix)
        {
            double gap = pair.Value / weights * total - (counts.TryGetValue(pair.Key, out int c) ? c : 0);
            if (gap > bestGap)
            {
                bestGap = gap;
                best = pair.Key;
            }
        }
        return best;
    }
}
