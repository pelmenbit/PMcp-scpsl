using System;
using CommandSystem;
using LabApi.Features.Console;
using LabApi.Features.Permissions;
using PMcp.Ai;
using PMcp.Director;
using PMcp.Protocol;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Commands;

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public class PmcpCommand : ICommand
{
    public string Command => "pmcp";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "PMcp: status | ai <задача> | director ... | confirm/deny <id> | pending";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        Main? main = Main.Instance;
        if (main == null)
        {
            response = "PMcp не загружен";
            return false;
        }
        if (!sender.HasPermission(main.PluginConfig.Permission))
        {
            response = $"Нужно право {main.PluginConfig.Permission}";
            return false;
        }

        string sub = arguments.Count > 0 ? arguments.At(0).ToLowerInvariant() : "status";
        switch (sub)
        {
            case "status":
                response = main.StatusText();
                return true;

            case "ai":
                AiAgent? agent = main.Agent;
                if (agent == null)
                {
                    response = "ИИ выключен: ai.enabled: false в config.yml PMcp";
                    return false;
                }

                string task = string.Join(" ", arguments.Array, arguments.Offset + 1, arguments.Count - 1).Trim();
                if (!agent.TryStart(task, null, result => Report(result, sender), out string error))
                {
                    response = error.Length > 0 ? error : "Использование: pmcp ai <задача>";
                    return false;
                }
                response = $"Запущено ({main.PluginConfig.Ai.Model}), ответ придёт сюда же через несколько секунд";
                return true;

            case "director":
                return Director(main, arguments, sender, out response);

            case "confirm":
            case "deny":
            case "pending":
                return Mcp(main, sub, arguments, out response);

            case "dummies":
                if (arguments.Count < 2 || (arguments.At(1) != "on" && arguments.At(1) != "off"))
                {
                    response = $"Боты считаются игроками: {(PlayerInfo.CountDummies ? "да" : "нет")}. pmcp dummies on|off";
                    return arguments.Count < 2;
                }
                PlayerInfo.CountDummies = arguments.At(1) == "on";
                response = PlayerInfo.CountDummies ? "Боты считаются игроками до перезапуска" : "Боты больше не считаются игроками";
                return true;

            default:
                response = Usage;
                return false;
        }
    }

    private const string Usage =
        "Использование: pmcp status | pmcp ai <задача> | pmcp director [status|on|off|dry on|dry off|plan|replan|prepare|approve|reject|"
        + "pause|resume|skip|cancel|log|stats|bench [игроков]|test <триггер|tail|end>] | pmcp pending | pmcp confirm <id> | pmcp deny <id> | pmcp dummies [on|off]";

    private static bool Mcp(Main main, string sub, ArraySegment<string> arguments, out string response)
    {
        McpActions? actions = main.McpActions;
        if (actions == null)
        {
            response = "Действия через MCP выключены: mcp_write_tools: false в config.yml PMcp";
            return false;
        }
        if (sub == "pending")
        {
            response = actions.List();
            return true;
        }
        if (arguments.Count < 2)
        {
            response = $"pmcp {sub} <id> - id из pmcp pending";
            return false;
        }
        string id = arguments.At(1);
        return sub == "confirm" ? actions.ConfirmAction(id, out response) : actions.Deny(id, out response);
    }

    private static bool Director(Main main, ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        RoundDirector? director = main.Director;
        if (director == null)
        {
            response = "Дирижёр не запущен: нужен ai.enabled: true в config.yml PMcp";
            return false;
        }

        DirectorConfig cfg = main.PluginConfig.Director;
        string sub = arguments.Count > 1 ? arguments.At(1).ToLowerInvariant() : "status";
        switch (sub)
        {
            case "status":
                response = director.Status();
                return true;
            case "on":
                cfg.Enabled = true;
                response = "Дирижёр включён до перезапуска. " + (cfg.DryRun ? "Режим DRY RUN." : "Исполняет действия!");
                return true;
            case "off":
                cfg.Enabled = false;
                response = "Дирижёр выключен до перезапуска";
                return true;
            case "dry":
                if (arguments.Count < 3 || (arguments.At(2) != "on" && arguments.At(2) != "off"))
                {
                    response = "pmcp director dry on|off";
                    return false;
                }
                cfg.DryRun = arguments.At(2) == "on";
                response = cfg.DryRun ? "DRY RUN: действия только в консоль" : "Дирижёр ИСПОЛНЯЕТ действия в игре";
                return true;
            case "plan":
                response = director.PlanText();
                return true;
            case "replan":
                bool ok = director.Replan((text, success) => Reply(sender, text, success), out string message);
                response = message;
                return ok;
            case "prepare":
                ok = director.Prepare((text, success) => Reply(sender, text, success), out message);
                response = message;
                return ok;
            case "approve":
                return director.Approve(out response);
            case "reject":
                return director.Reject(out response);
            case "pause":
                return director.Pause(true, out response);
            case "resume":
                return director.Pause(false, out response);
            case "skip":
                return director.SkipNext(out response);
            case "cancel":
                return director.Cancel(out response);
            case "stats":
                response = director.StatsText();
                return true;
            case "bench":
                int players = 8;
                if (arguments.Count > 2 && !int.TryParse(arguments.At(2), out players))
                {
                    response = "pmcp director bench [игроков 1-100]";
                    return false;
                }
                ok = director.Bench(players, (text, success) => Reply(sender, text, success), out message);
                response = message;
                return ok;
            case "log":
                response = director.LogText();
                return true;
            case "test":
                string kind = arguments.Count > 2 ? arguments.At(2).ToLowerInvariant() : "";
                ok = director.Test(kind, (text, success) => Reply(sender, text, success), out message);
                response = message;
                return ok;
            default:
                response = Usage;
                return false;
        }
    }

    private static void Report(AgentResult result, ICommandSender sender)
    {
        string stats =
            $"раундов {result.Rounds}, вызовов инструментов {result.ToolCalls}, "
            + $"токены {result.PromptTokens} вход / {result.CompletionTokens} выход, {result.Seconds:0.0} с";
        string text = result.Success
            ? $"ИИ ({stats}):\n{TextSanitizer.StripTags(result.Text)}"
            : $"ИИ не ответил ({stats}): {TextSanitizer.StripTags(result.Text)}";

        if (result.Success)
            Logger.Info(text);
        else
            Logger.Warn(text);

        Reply(sender, text, result.Success);
    }

    private static void Reply(ICommandSender sender, string text, bool success)
    {
        if (sender.GetType().Name == "ServerConsoleSender")
            return;
        try
        {
            sender.Respond("PMCP#" + text, success);
        }
        catch
        {
        }
    }
}
