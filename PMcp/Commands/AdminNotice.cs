using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using PMcp.Util;

namespace PMcp.Commands;

public static class AdminNotice
{
    public static void Send(string text)
    {
        Logger.Info(text);
        string clean = TextSanitizer.StripTags(text);
        foreach (Player p in Player.List)
        {
            if (p == null || p.IsHost || p.IsDestroyed || !p.RemoteAdminAccess)
                continue;
            try
            {
                p.SendConsoleMessage("[PMcp] " + clean, "yellow");
            }
            catch
            {
            }
        }
    }
}
