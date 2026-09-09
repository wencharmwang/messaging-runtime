using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace WeChatDriver.Mcp;

/// <summary>
/// MCP 的 Streamable HTTP 传输：仅监听 127.0.0.1。
/// POST /mcp → JSON-RPC 请求/响应；GET /mcp → 服务信息；DELETE /mcp → 会话结束。
/// 可选 Bearer 令牌校验（--mcp-token 提供时启用）。
/// </summary>
public sealed class McpHttpTransport : IDisposable
{
    private readonly McpServer _server;
    private readonly HttpListener _listener;
    private readonly string? _token;
    private CancellationTokenSource? _cts;

    public int Port { get; }

    public McpHttpTransport(McpServer server, int port = 0, string? token = null)
    {
        _server = server;
        _token = token;
        _listener = new HttpListener();
        var listenPort = port > 0 ? port : PickPort();
        Port = listenPort;
        _listener.Prefixes.Add($"http://127.0.0.1:{listenPort}/");
    }

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { continue; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            if (_token is not null)
            {
                var auth = ctx.Request.Headers["Authorization"];
                if (!string.Equals(auth, "Bearer " + _token, StringComparison.Ordinal))
                {
                    Respond(ctx, 401, "{\"error\":\"unauthorized\"}");
                    return;
                }
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path != "/mcp" && path != "/")
            {
                Respond(ctx, 404, "{\"error\":\"not_found\"}");
                return;
            }

            switch (ctx.Request.HttpMethod)
            {
                case "POST":
                    HandlePost(ctx);
                    break;
                case "GET":
                    Respond(ctx, 200, "{\"status\":\"mcp-server\",\"name\":\"wechat-driver\"}");
                    break;
                case "DELETE":
                    Respond(ctx, 200, "{}");
                    break;
                default:
                    Respond(ctx, 405, "{\"error\":\"method_not_allowed\"}");
                    break;
            }
        }
        catch
        {
            // 忽略单个请求错误。
        }
        finally
        {
            ctx.Response.Close();
        }
    }

    private void HandlePost(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = reader.ReadToEnd();
        var response = _server.Handle(body);

        if (response is null)
        {
            // 通知：202 Accepted，空体。
            Respond(ctx, 202, "");
        }
        else
        {
            Respond(ctx, 200, response);
        }
    }

    private static void Respond(HttpListenerContext ctx, int code, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static int PickPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
    }

    public void Dispose() => Stop();
}
