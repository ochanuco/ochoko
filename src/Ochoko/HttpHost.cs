using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ochoko.Core;

namespace Ochoko;

public sealed class HttpHost
{
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly TimeSpan AcceptBackoff = TimeSpan.FromMilliseconds(500);

    private readonly HttpListener _listener = new HttpListener();
    private readonly McpServer _mcp;
    private readonly BearerAuthenticator _auth;
    private readonly IVoiceEngine _engine;
    private readonly string _version;
    private readonly ILog _log;

    public HttpHost(string prefix, McpServer mcp, BearerAuthenticator auth, IVoiceEngine engine, string version, ILog log)
    {
        _mcp = mcp;
        _auth = auth;
        _engine = engine;
        _version = version;
        _log = log;
        _listener.Prefixes.Add(prefix);
    }

    public void Start() => _listener.Start();

    /// <summary>ct が取り消されるまで受け付ける。リスナー自体が止まったときは例外を投げる。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using (ct.Register(() => _listener.Close()))
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!_listener.IsListening)
                    {
                        throw new InvalidOperationException("the HTTP listener stopped: " + e.Message, e);
                    }

                    // 接続単位の一時的な失敗。リスナーは生きているので待って続ける
                    _log.Warn("accept failed: " + e.Message);
                    try
                    {
                        await Task.Delay(AcceptBackoff, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                _ = Task.Run(() => HandleAsync(context, ct));
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/healthz" && context.Request.HttpMethod == "GET")
            {
                var body = new JsonObject
                {
                    ["status"] = "ok",
                    ["version"] = _version,
                    ["engine"] = _engine.State.ToString().ToLowerInvariant(),
                };
                await WriteAsync(context, 200, body.ToJsonString()).ConfigureAwait(false);
            }
            else if (path == "/mcp")
            {
                await HandleMcpAsync(context, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteAsync(context, 404, null).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _log.Error("request failed: " + e);
            try
            {
                await WriteAsync(context, 500, null).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ヘッダー送出後などで 500 を書けない。接続ごと切る
                context.Response.Abort();
            }
        }
    }

    private async Task HandleMcpAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;

        // 呼び出し元はサーバー間なので Origin を送らない。付いていればブラウザ経由とみなして拒否する
        if (request.Headers["Origin"] != null)
        {
            await WriteAsync(context, 403, null).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod != "POST")
        {
            context.Response.AddHeader("Allow", "POST");
            await WriteAsync(context, 405, null).ConfigureAwait(false);
            return;
        }

        var caller = _auth.Authenticate(request.Headers["Authorization"]);
        if (caller == null)
        {
            context.Response.AddHeader("WWW-Authenticate", "Bearer");
            await WriteAsync(context, 401, null).ConfigureAwait(false);
            return;
        }

        if (request.ContentLength64 > MaxBodyBytes)
        {
            await WriteAsync(context, 413, null).ConfigureAwait(false);
            return;
        }

        var text = await ReadBodyAsync(request).ConfigureAwait(false);
        if (text == null)
        {
            await WriteAsync(context, 413, null).ConfigureAwait(false);
            return;
        }

        var response = await _mcp.HandleAsync(text, caller, ct, request.Headers["MCP-Protocol-Version"]).ConfigureAwait(false);
        await WriteAsync(context, response.Status, response.Body).ConfigureAwait(false);
    }

    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await request.InputStream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBodyBytes)
                {
                    return null;
                }
            }

            return new UTF8Encoding(false).GetString(buffer.ToArray());
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string? body)
    {
        var response = context.Response;
        response.StatusCode = status;
        if (body == null)
        {
            response.ContentLength64 = 0;
        }
        else
        {
            var bytes = new UTF8Encoding(false).GetBytes(body);
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        response.Close();
    }
}
