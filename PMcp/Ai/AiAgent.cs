using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using PMcp.Threading;
using PMcp.Tools;
using PMcp.Util;

namespace PMcp.Ai;

public sealed class AgentResult
{
    public bool Success { get; set; }
    public string Text { get; set; } = "";
    public int Rounds { get; set; }
    public int ToolCalls { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
    public double Seconds { get; set; }
    public bool Cancelled { get; set; }
}

public sealed class AgentRun
{
    private readonly object _lock = new();
    private System.Net.HttpWebRequest? _request;
    private volatile bool _cancelled;

    public bool Cancelled => _cancelled;

    public void Cancel()
    {
        _cancelled = true;
        lock (_lock)
        {
            try
            {
                _request?.Abort();
            }
            catch (Exception)
            {
            }
        }
    }

    internal void Attach(System.Net.HttpWebRequest? request)
    {
        lock (_lock)
        {
            _request = request;
            if (_cancelled && request != null)
                request.Abort();
        }
    }
}

public sealed class AgentRunOptions
{
    public ToolRunner? Runner { get; set; }
    public int? MaxToolRounds { get; set; }
    public int? MaxOutputTokens { get; set; }

    public string? Model { get; set; }

    public string? RequiredTool { get; set; }

    public AgentRun? Run { get; set; }
}

public sealed class AiAgent
{
    private static readonly Regex ThinkBlock = new(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase);
    private static readonly Regex JsonFence = new(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);

    private readonly AiConfig _config;
    private readonly ToolRunner _runner;
    private readonly MainThreadQueue _queue;
    private readonly OpenAiClient _client;
    private readonly TokenLedger _ledger;
    private readonly Dictionary<ToolRunner, List<object>> _toolDefinitions = new();
    private int _running;
    private volatile bool _stopped;

    public AiAgent(AiConfig config, ToolRunner runner, MainThreadQueue queue, TokenLedger ledger)
    {
        _config = config;
        _runner = runner;
        _queue = queue;
        _ledger = ledger;
        _client = new OpenAiClient(config);
    }

    public TokenLedger Ledger => _ledger;

    public int Running => Volatile.Read(ref _running);
    public string Endpoint => _client.Endpoint;

    public bool TryStart(string task, string? systemPrompt, Action<AgentResult> onDone, out string error) =>
        TryStart(task, systemPrompt, null, onDone, out error);

    public bool TryStart(string task, string? systemPrompt, AgentRunOptions? options, Action<AgentResult> onDone, out string error)
    {
        if (_stopped)
        {
            error = "ИИ остановлен";
            return false;
        }
        if (string.IsNullOrWhiteSpace(task))
        {
            error = "Пустая задача";
            return false;
        }
        if (!_ledger.Allow(out error))
            return false;
        if (Interlocked.Increment(ref _running) > Math.Max(1, _config.MaxConcurrentRuns))
        {
            Interlocked.Decrement(ref _running);
            error = $"Уже идёт {_config.MaxConcurrentRuns} прогона ИИ, дождись окончания";
            return false;
        }

        ToolRunner runner = options?.Runner ?? _runner;
        if (!_toolDefinitions.TryGetValue(runner, out List<object> definitions))
            _toolDefinitions[runner] = definitions = BuildToolDefinitions(runner);
        int maxRounds = Math.Max(1, options?.MaxToolRounds ?? _config.MaxToolRounds) + 1;
        int maxTokens = options?.MaxOutputTokens ?? _config.MaxOutputTokens;
        string? requiredTool = options?.RequiredTool;
        string model = string.IsNullOrWhiteSpace(options?.Model) ? _config.Model : options!.Model!.Trim();
        AgentRun run = options?.Run ?? new AgentRun();

        var thread = new Thread(() =>
        {
            AgentResult result;
            try
            {
                result = Run(task, systemPrompt ?? _config.SystemPrompt, runner, definitions, maxRounds, maxTokens, requiredTool, model, run);
            }
            catch (Exception e)
            {
                result = new AgentResult { Success = false, Text = "Агент упал: " + e.Message };
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
            _queue.Post(() =>
            {
                _ledger.Add(result);
                onDone(result);
            });
        })
        {
            IsBackground = true,
            Name = "PMcp.Ai",
        };
        thread.Start();

        error = "";
        return true;
    }

    public void Stop()
    {
        _stopped = true;
        _client.Stop();
    }

    private AgentResult Run(string task, string systemPrompt, ToolRunner runner, List<object> definitions, int maxRounds, int maxTokens, string? requiredTool, string model, AgentRun run)
    {
        Stopwatch watch = Stopwatch.StartNew();
        var result = new AgentResult();
        var messages = new List<object>
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = task },
        };

        bool requiredDone = requiredTool == null;
        try
        {
            for (int round = 1; round <= maxRounds; round++)
            {
                if (_stopped)
                    throw new AiException("ИИ остановлен");
                if (run.Cancelled)
                    throw new AiException("Прогон отменён");

                result.Rounds = round;
                bool lastRound = round == maxRounds;
                Dictionary<string, object?> response;
                try
                {
                    response = _client.Chat(BuildRequest(model, messages, definitions, maxTokens, lastRound ? "none" : "auto"), run);
                }
                catch (AiException e) when (lastRound && e.Status == 400)
                {
                    response = _client.Chat(BuildRequest(model, messages, definitions, maxTokens, null), run);
                }
                AddUsage(result, response);

                Dictionary<string, object?> message = FirstMessage(response);
                List<object?>? toolCalls = message.TryGetValue("tool_calls", out object? tc) ? tc as List<object?> : null;

                if (toolCalls == null || toolCalls.Count == 0)
                {
                    string content = message.TryGetValue("content", out object? c) ? c as string ?? "" : "";
                    content = ThinkBlock.Replace(content, "").Trim();

                    if (!requiredDone)
                    {
                        string feedback;
                        if (TryExtractJsonObject(content, out Dictionary<string, object?> textArgs))
                        {
                            result.ToolCalls++;
                            ToolCallResult outcome = runner.Call(requiredTool!, textArgs, "ai-text");
                            if (!outcome.IsError)
                            {
                                result.Success = true;
                                result.Text = $"{requiredTool} принят (аргументы взяты из текста ответа)";
                                return result;
                            }
                            feedback = $"Your JSON was passed to {requiredTool}, it returned an error:\n{outcome.Text}\nFix exactly that and send the whole thing again.";
                        }
                        else
                            feedback =
                                $"A text answer does not count. Call the tool {requiredTool} as a function call "
                                + "or as a single JSON object with its arguments exactly as described.";

                        if (round + 1 < maxRounds)
                        {
                            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.Length > 0 ? content : "." });
                            messages.Add(new JsonObject { ["role"] = "user", ["content"] = feedback });
                            continue;
                        }
                    }
                    result.Success = true;
                    result.Text = content.Length > 0 ? content : "(модель вернула пустой ответ)";
                    return result;
                }

                var assistant = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = message.TryGetValue("content", out object? ac) ? ac : null,
                    ["tool_calls"] = CleanToolCalls(toolCalls),
                };
                if (message.TryGetValue("reasoning_content", out object? reasoning) && reasoning is string)
                    assistant["reasoning_content"] = reasoning;
                messages.Add(assistant);

                foreach (object? call in toolCalls)
                {
                    result.ToolCalls++;
                    messages.Add(ExecuteCall(runner, call as Dictionary<string, object?>, out string called, out bool failed));
                    if (requiredTool != null && called == requiredTool)
                        requiredDone = !failed;
                }
            }

            result.Text = "Модель не уложилась в лимит раундов";
            return result;
        }
        catch (AiException e)
        {
            result.Success = false;
            result.Cancelled = run.Cancelled;
            result.Text = e.Message;
            return result;
        }
        finally
        {
            result.Seconds = watch.Elapsed.TotalSeconds;
        }
    }

    private JsonObject BuildRequest(string model, List<object> messages, List<object> definitions, int maxTokens, string? toolChoice)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["tools"] = definitions,
        };
        if (toolChoice != null)
            body["tool_choice"] = toolChoice;
        if (maxTokens > 0 && !string.IsNullOrEmpty(_config.MaxTokensParam))
            body[_config.MaxTokensParam] = maxTokens;
        if (_config.Temperature >= 0)
            body["temperature"] = _config.Temperature;
        return body;
    }

    private static JsonObject ExecuteCall(ToolRunner runner, Dictionary<string, object?>? call, out string name, out bool failed)
    {
        string id = call != null && call.TryGetValue("id", out object? i) ? i as string ?? "" : "";
        var function = call != null && call.TryGetValue("function", out object? f) ? f as Dictionary<string, object?> : null;
        name = function != null && function.TryGetValue("name", out object? n) ? n as string ?? "" : "";

        string content;
        Dictionary<string, object?>? args = ParseArguments(function);
        if (args == null)
        {
            content = "Error: arguments is not a JSON object, repeat the call with valid arguments";
            failed = true;
        }
        else
        {
            ToolCallResult outcome = runner.Call(name, args, "ai");
            content = outcome.IsError ? "Error: " + outcome.Text : outcome.Text;
            failed = outcome.IsError;
        }

        return new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = id,
            ["content"] = content,
        };
    }

    private static bool TryExtractJsonObject(string text, out Dictionary<string, object?> args)
    {
        args = new Dictionary<string, object?>(StringComparer.Ordinal);
        Match fence = JsonFence.Match(text);
        string candidate = fence.Success ? fence.Groups[1].Value : text;

        int start = candidate.IndexOf('{');
        int end = candidate.LastIndexOf('}');
        if (start < 0 || end <= start)
            return false;

        try
        {
            if (Json.Parse(candidate.Substring(start, end - start + 1)) is Dictionary<string, object?> parsed)
            {
                args = parsed;
                return true;
            }
        }
        catch (FormatException) { }
        return false;
    }

    private static List<object> CleanToolCalls(List<object?> toolCalls)
    {
        var clean = new List<object>(toolCalls.Count);
        foreach (object? item in toolCalls)
        {
            if (item is not Dictionary<string, object?> call)
                continue;
            var function = call.TryGetValue("function", out object? f) ? f as Dictionary<string, object?> : null;
            object? arguments = function != null && function.TryGetValue("arguments", out object? a) ? a : null;
            clean.Add(new JsonObject
            {
                ["id"] = call.TryGetValue("id", out object? id) ? id : "",
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = function != null && function.TryGetValue("name", out object? n) ? n : "",
                    ["arguments"] = arguments is string s ? s : Json.Write(arguments ?? new JsonObject()),
                },
            });
        }
        return clean;
    }

    private static Dictionary<string, object?>? ParseArguments(Dictionary<string, object?>? function)
    {
        if (function == null || !function.TryGetValue("arguments", out object? raw) || raw == null)
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        if (raw is Dictionary<string, object?> dict)
            return dict;
        if (raw is not string text)
            return null;
        if (string.IsNullOrWhiteSpace(text))
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            return Json.Parse(text) as Dictionary<string, object?>;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> FirstMessage(Dictionary<string, object?> response)
    {
        if (response.TryGetValue("choices", out object? c)
            && c is List<object?> choices
            && choices.Count > 0
            && choices[0] is Dictionary<string, object?> choice
            && choice.TryGetValue("message", out object? m)
            && m is Dictionary<string, object?> message)
            return message;

        throw new AiException("В ответе провайдера нет choices[0].message");
    }

    private static void AddUsage(AgentResult result, Dictionary<string, object?> response)
    {
        if (!response.TryGetValue("usage", out object? u) || u is not Dictionary<string, object?> usage)
            return;
        if (usage.TryGetValue("prompt_tokens", out object? p) && p is double prompt)
            result.PromptTokens += (long)prompt;
        if (usage.TryGetValue("completion_tokens", out object? c) && c is double completion)
            result.CompletionTokens += (long)completion;
        if (usage.TryGetValue("prompt_cache_hit_tokens", out object? h) && h is double hit)
            result.CachedTokens += (long)hit;
        else if (usage.TryGetValue("prompt_tokens_details", out object? d) && d is Dictionary<string, object?> details
            && details.TryGetValue("cached_tokens", out object? ct) && ct is double cached)
            result.CachedTokens += (long)cached;
    }

    private static List<object> BuildToolDefinitions(ToolRunner runner)
    {
        var list = new List<object>(runner.Tools.Count);
        foreach (ITool tool in runner.Tools)
        {
            list.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.InputSchema,
                },
            });
        }
        return list;
    }
}
