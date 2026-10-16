using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using PMcp.Util;

namespace PMcp.Ai;

public sealed class AiException : Exception
{
    public int Status { get; }

    public AiException(string message, int status = 0)
        : base(message)
    {
        Status = status;
    }
}

public sealed class OpenAiClient
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly int[] RetryStatuses = { 429, 500, 502, 503, 504 };
    private static readonly int[] RetryDelaysMs = { 2000, 5000 };

    private readonly AiConfig _config;
    private readonly HashSet<HttpWebRequest> _active = new();
    private volatile bool _stopped;

    public OpenAiClient(AiConfig config)
    {
        _config = config;
    }

    public string Endpoint => _config.BaseUrl.TrimEnd('/') + "/chat/completions";

    public Dictionary<string, object?> Chat(JsonObject body, AgentRun? run = null)
    {
        string payload = Json.Write(body);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return Send(payload, run);
            }
            catch (AiException e) when (attempt < RetryDelaysMs.Length && Array.IndexOf(RetryStatuses, e.Status) >= 0 && !_stopped && run?.Cancelled != true)
            {
                Wait(RetryDelaysMs[attempt], run);
            }
        }
    }

    private void Wait(int ms, AgentRun? run)
    {
        for (int waited = 0; waited < ms && !_stopped && run?.Cancelled != true; waited += 100)
            Thread.Sleep(100);
    }

    private Dictionary<string, object?> Send(string payload, AgentRun? run)
    {
        if (_stopped)
            throw new AiException("ИИ остановлен");
        if (run?.Cancelled == true)
            throw new AiException("Прогон отменён");

        var request = (HttpWebRequest)WebRequest.Create(Endpoint);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Accept = "application/json";
        request.UserAgent = "PMcp";
        request.Timeout = request.ReadWriteTimeout = Math.Max(5, _config.RequestTimeoutSeconds) * 1000;
        if (!string.IsNullOrEmpty(_config.ApiKey))
            request.Headers["Authorization"] = "Bearer " + _config.ApiKey;

        lock (_active)
            _active.Add(request);
        run?.Attach(request);

        try
        {
            byte[] bytes = Utf8.GetBytes(payload);
            request.ContentLength = bytes.Length;
            using (Stream stream = request.GetRequestStream())
                stream.Write(bytes, 0, bytes.Length);

            string text;
            using (var response = (HttpWebResponse)request.GetResponse())
                text = ReadAll(response);

            if (Json.Parse(text) is not Dictionary<string, object?> parsed)
                throw new AiException("Провайдер вернул не JSON-объект");
            return parsed;
        }
        catch (WebException) when (run?.Cancelled == true)
        {
            throw new AiException("Прогон отменён");
        }
        catch (WebException e) when (e.Response is HttpWebResponse error)
        {
            int status = (int)error.StatusCode;
            throw new AiException($"HTTP {status}: {ProviderMessage(ReadAll(error))}{Hint(status)}", status);
        }
        catch (WebException e)
        {
            if (_stopped)
                throw new AiException("ИИ остановлен");
            throw new AiException($"Нет связи с {request.RequestUri.Host}: {e.Status} - {e.Message}");
        }
        catch (FormatException e)
        {
            throw new AiException("Провайдер вернул кривой JSON: " + e.Message);
        }
        finally
        {
            run?.Attach(null);
            lock (_active)
                _active.Remove(request);
        }
    }

    public void Stop()
    {
        _stopped = true;
        lock (_active)
        {
            foreach (HttpWebRequest request in _active)
            {
                try
                {
                    request.Abort();
                }
                catch { }
            }
            _active.Clear();
        }
    }

    private static string ReadAll(HttpWebResponse response)
    {
        using Stream stream = response.GetResponseStream();
        using var reader = new StreamReader(stream, Utf8);
        return reader.ReadToEnd();
    }

    private static string ProviderMessage(string body)
    {
        try
        {
            if (Json.Parse(body) is Dictionary<string, object?> root)
            {
                if (root.TryGetValue("error", out object? error))
                {
                    if (error is string s)
                        return s;
                    if (error is Dictionary<string, object?> e && e.TryGetValue("message", out object? m) && m is string message)
                        return message;
                }
                if (root.TryGetValue("detail", out object? detail) && detail != null)
                    return detail as string ?? Json.Write(detail);
            }
        }
        catch (FormatException) { }

        return body.Length > 300 ? body.Substring(0, 300) + "…" : body;
    }

    private static string Hint(int status) =>
        status switch
        {
            401 => " - проверь ai.api_key (ключ провайдера, у ProxyAPI начинается с sk-) и ai.base_url",
            402 => " - закончился баланс или лимит ключа у провайдера",
            403 => " - модель или эндпоинт запрещены для этого ключа",
            404 => " - проверь ai.model: у ProxyAPI полный id вида вендор/модель",
            429 => " - лимит запросов провайдера, повторы не помогли",
            _ => "",
        };
}
