using System;
using System.Collections.Generic;
using System.Linq;
using PMcp.Commands;
using PMcp.Director;
using PMcp.Tools;
using PMcp.Util;
using UnityEngine;

namespace PMcp.Protocol;

public sealed class McpPendingAction
{
    public string Id { get; set; } = "";
    public ActionTool Tool { get; set; } = null!;
    public Dictionary<string, object?> Args { get; set; } = new();
    public string Summary { get; set; } = "";
    public float ExpiresAt { get; set; }
}

public sealed class McpActions
{
    private readonly Config _config;
    private readonly DirectorContext _ctx;
    private readonly Dictionary<string, McpPendingAction> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Random _random = new();

    public McpActions(Config config, DirectorContext ctx)
    {
        _config = config;
        _ctx = ctx;
    }

    public IEnumerable<ITool> Wrap(IEnumerable<ActionTool> actions) => actions.Select(a => (ITool)new McpActionTool(a, this));

    public bool Confirm => _config.McpConfirm;

    public object Run(ActionTool tool, Dictionary<string, object?> args)
    {
        _ctx.ActionSource = "mcp";
        try
        {
            return tool.Execute(args);
        }
        finally
        {
            _ctx.ActionSource = "ai";
        }
    }

    public object Queue(ActionTool tool, Dictionary<string, object?> args)
    {
        if (!_ctx.IsAllowed(tool.Name))
            throw new ArgumentException($"Action {tool.Name} is disabled (director.allowed_actions)");
        PreparedAction prepared = tool.Prepare(args);
        Expire();
        if (_pending.Count >= Math.Max(1, _config.McpMaxPending))
            throw new ArgumentException($"{_pending.Count} actions are already waiting for the admin - wait for them to be confirmed or denied");

        string id;
        do
            id = _random.Next(0x1000, 0xFFFF).ToString("x4");
        while (_pending.ContainsKey(id));

        var pending = new McpPendingAction
        {
            Id = id,
            Tool = tool,
            Args = args,
            Summary = $"{tool.Name}: {TextSanitizer.StripTags(prepared.Summary)}",
            ExpiresAt = Time.realtimeSinceStartup + Math.Max(10, _config.McpConfirmTimeoutSeconds),
        };
        _pending[id] = pending;
        AdminNotice.Send($"MCP просит действие {pending.Summary} - pmcp confirm {id} / pmcp deny {id} (ждёт {_config.McpConfirmTimeoutSeconds} с)");
        return new JsonObject
        {
            ["pending"] = true,
            ["id"] = id,
            ["summary"] = pending.Summary,
            ["note"] = "Queued: a server admin must confirm it in the console (pmcp confirm " + id + "). Do not repeat the call.",
        };
    }

    public bool ConfirmAction(string id, out string message)
    {
        Expire();
        if (!_pending.TryGetValue(id, out McpPendingAction pending))
        {
            message = $"Нет ожидающего действия {id} (истекло или уже обработано). pmcp pending - список";
            return false;
        }
        _pending.Remove(id);
        try
        {
            object result = Run(pending.Tool, pending.Args);
            message = $"Выполнено {pending.Summary}: {TextSanitizer.Clean(Json.Write(result), 300)}";
            return true;
        }
        catch (ArgumentException e)
        {
            message = $"Не выполнено {pending.Summary}: {e.Message}";
            return false;
        }
    }

    public bool Deny(string id, out string message)
    {
        Expire();
        if (!_pending.Remove(id))
        {
            message = $"Нет ожидающего действия {id}";
            return false;
        }
        message = $"Действие {id} отклонено";
        return true;
    }

    public string List()
    {
        Expire();
        if (_pending.Count == 0)
            return "Действий MCP, ждущих подтверждения, нет";
        float now = Time.realtimeSinceStartup;
        return "Ждут подтверждения:\n" + string.Join("\n", _pending.Values.Select(p => $"  {p.Id}  {p.Summary}  (ещё {(int)(p.ExpiresAt - now)} с)"));
    }

    private void Expire()
    {
        float now = Time.realtimeSinceStartup;
        foreach (string id in _pending.Values.Where(p => p.ExpiresAt <= now).Select(p => p.Id).ToList())
            _pending.Remove(id);
    }
}

public sealed class McpActionTool : IWriteTool
{
    private readonly ActionTool _inner;
    private readonly McpActions _gate;

    public McpActionTool(ActionTool inner, McpActions gate)
    {
        _inner = inner;
        _gate = gate;
    }

    public string Name => _inner.Name;
    public string Title => _inner.Title;

    public string Description =>
        _gate.Confirm
            ? _inner.Description + " CHANGES THE GAME: the call is queued until a server admin confirms it in the console."
            : _inner.Description + " CHANGES THE GAME immediately.";

    public JsonObject InputSchema => _inner.InputSchema;

    public object Execute(Dictionary<string, object?> args) => _gate.Confirm ? _gate.Queue(_inner, args) : _gate.Run(_inner, args);
}
