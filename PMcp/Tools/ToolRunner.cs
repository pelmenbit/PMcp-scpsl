using System;
using System.Collections.Generic;
using System.Diagnostics;
using LabApi.Features.Console;
using PMcp.Threading;
using PMcp.Util;

namespace PMcp.Tools;

public sealed class ToolCallResult
{
    public bool IsError { get; }
    public string Text { get; }
    public object? Structured { get; }

    private ToolCallResult(bool isError, string text, object? structured)
    {
        IsError = isError;
        Text = text;
        Structured = structured;
    }

    public static ToolCallResult Ok(object? result) => new(false, Json.Write(result), result);

    public static ToolCallResult Fail(string message) => new(true, message, null);
}

public sealed class ToolRunner
{
    private readonly Config _config;
    private readonly MainThreadQueue _queue;
    private readonly Dictionary<string, ITool> _byName = new(StringComparer.Ordinal);

    public IReadOnlyList<ITool> Tools { get; }

    public ToolRunner(Config config, MainThreadQueue queue, IEnumerable<ITool> tools)
    {
        _config = config;
        _queue = queue;

        var list = new List<ITool>(tools);
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (ITool tool in list)
            _byName[tool.Name] = tool;
        Tools = list;
    }

    public bool Has(string? name) => name != null && _byName.ContainsKey(name);

    public ToolCallResult Call(string name, Dictionary<string, object?> args, string source)
    {
        if (!_byName.TryGetValue(name, out ITool tool))
            return ToolCallResult.Fail($"Unknown tool '{name}'");

        try
        {
            object? result = _queue.Run(() => Execute(tool, args, source), _config.MainThreadTimeoutMs);
            return ToolCallResult.Ok(result);
        }
        catch (ArgumentException e)
        {
            Warn($"{source} {name} -> отказ: {Short(e.Message)}");
            return ToolCallResult.Fail(e.Message);
        }
        catch (TimeoutException e)
        {
            Warn($"{name}: {e.Message}");
            return ToolCallResult.Fail(e.Message);
        }
        catch (MainThreadUnavailableException e)
        {
            return ToolCallResult.Fail(e.Message);
        }
        catch (Exception e)
        {
            Warn($"{name} упал: {e}");
            return ToolCallResult.Fail("Internal tool error: " + e.Message);
        }
    }

    public ToolCallResult ExecuteInline(string name, Dictionary<string, object?> args, string source)
    {
        if (!_byName.TryGetValue(name, out ITool tool))
            return ToolCallResult.Fail($"Unknown tool '{name}'");

        try
        {
            return ToolCallResult.Ok(Execute(tool, args, source));
        }
        catch (ArgumentException e)
        {
            return ToolCallResult.Fail(e.Message);
        }
        catch (Exception e)
        {
            Logger.Warn($"{name} упал: {e}");
            return ToolCallResult.Fail("Internal tool error: " + e.Message);
        }
    }

    public ITool? Find(string name) => _byName.TryGetValue(name, out ITool tool) ? tool : null;

    private object Execute(ITool tool, Dictionary<string, object?> args, string source)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            return tool.Execute(args);
        }
        finally
        {
            if (_config.LogCalls)
                Logger.Info(
                    $"{source} {tool.Name} {TextSanitizer.Clean(Json.Write(args), 300)} - {watch.Elapsed.TotalMilliseconds:0.00} мс"
                );
        }
    }

    private void Warn(string message) => _queue.Post(() => Logger.Warn(message));

    private static string Short(string text)
    {
        string clean = TextSanitizer.StripTags(text).Replace('\n', ' ');
        return clean.Length > 500 ? clean.Substring(0, 500) + "…" : clean;
    }
}
