using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using PMcp.Protocol;

namespace PMcp.Transport;

public sealed class HttpServer
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly Config _config;
    private readonly McpDispatcher _dispatcher;
    private readonly Action<string> _warn;
    private readonly byte[] _token;

    private HttpListener? _listener;
    private volatile bool _stopped = true;
    private int _inFlight;

    public HttpServer(Config config, McpDispatcher dispatcher, Action<string> warn)
    {
        _config = config;
        _dispatcher = dispatcher;
        _warn = warn;
        _token = Utf8.GetBytes(config.Token ?? "");
    }

    public string Prefix => $"http://{_config.BindAddress}:{_config.Port}/";
    public void Start()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(Prefix);
        listener.Start();

        _listener = listener;
        _stopped = false;
        Accept(listener);
    }

    public void Stop()
    {
        _stopped = true;
        HttpListener? listener = _listener;
        _listener = null;
        if (listener == null)
            return;
        try
        {
            listener.Stop();
            listener.Close();
        }
        catch
        {}
    }

    private void Accept(HttpListener listener)
    {
        if (_stopped || listener != _listener)
            return;
        try
        {
            listener.BeginGetContext(OnContext, listener);
        }
        catch (Exception e)
        {
            if (!_stopped)
                _warn("Приём соединений остановился: " + e.Message);
        }
    }

    private void OnContext(IAsyncResult ar)
    {
        var listener = (HttpListener)ar.AsyncState;
        HttpListenerContext context;
        try
        {
            context = listener.EndGetContext(ar);
        }
        catch
        {
            return;
        }

        Accept(listener);

        try
        {
            Handle(context);
        }
        catch (Exception e)
        {
            _warn("Ошибка обработки запроса: " + e);
            TryWrite(context.Response, 500, null);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {}
        }
    }

    private void Handle(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        if (!string.Equals(request.Url.AbsolutePath.TrimEnd('/'), "/mcp", StringComparison.Ordinal))
        {
            TryWrite(response, 404, null);
            return;
        }

        string? origin = request.Headers["Origin"];
        if (origin != null && !_config.AllowedOrigins.Exists(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase)))
        {
            TryWrite(response, 403, ErrorBody(-32600, "Origin не разрешён"));
            return;
        }

        if (_config.RequireToken && !IsAuthorized(request.Headers["Authorization"]))
        {
            response.AddHeader("WWW-Authenticate", "Bearer");
            TryWrite(response, 401, ErrorBody(-32600, "Нужен заголовок Authorization: Bearer <Token из config.yml PMcp>"));
            return;
        }

        if (request.HttpMethod != "POST")
        {
            response.AddHeader("Allow", "POST");
            TryWrite(response, 405, null);
            return;
        }

        string? body = ReadBody(request);
        if (body == null)
        {
            TryWrite(response, 413, ErrorBody(-32600, $"Тело запроса больше {_config.MaxBodyBytes} байт"));
            return;
        }

        if (Interlocked.Increment(ref _inFlight) > Math.Max(1, _config.MaxConcurrentRequests))
        {
            Interlocked.Decrement(ref _inFlight);
            response.AddHeader("Retry-After", "1");
            TryWrite(response, 503, ErrorBody(-32000, "Слишком много одновременных запросов"));
            return;
        }

        try
        {
            RpcReply reply = _dispatcher.Handle(body, request.Headers);
            TryWrite(response, reply.Status, reply.Body);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private bool IsAuthorized(string? header)
    {
        const string scheme = "Bearer ";
        if (header == null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        byte[] given = Utf8.GetBytes(header.Substring(scheme.Length).Trim());
        if (given.Length != _token.Length || _token.Length == 0)
            return false;
        int diff = 0;
        for (int i = 0; i < given.Length; i++)
            diff |= given[i] ^ _token[i];
        return diff == 0;
    }

    private string? ReadBody(HttpListenerRequest request)
    {
        int max = Math.Max(1024, _config.MaxBodyBytes);
        if (request.ContentLength64 > max)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        Stream input = request.InputStream;
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max)
                return null;
        }

        Encoding encoding = request.ContentEncoding ?? Utf8;
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string ErrorBody(int code, string message) =>
        Util.Json.Write(new Util.JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = null,
            ["error"] = new Util.JsonObject { ["code"] = code, ["message"] = message },
        });

    private static void TryWrite(HttpListenerResponse response, int status, string? body)
    {
        try
        {
            response.StatusCode = status;
            if (body == null)
            {
                response.ContentLength64 = 0;
                return;
            }

            byte[] bytes = Utf8.GetBytes(body);
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch
        {}
    }
}
