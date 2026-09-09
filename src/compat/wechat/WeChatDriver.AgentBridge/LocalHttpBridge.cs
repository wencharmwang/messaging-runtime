using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using WeChatDriver.Core;

namespace WeChatDriver.AgentBridge;

/// <summary>
/// 仅限 127.0.0.1 的本地事件/命令桥。使用启动时生成的本地随机令牌，不监听局域网。
/// </summary>
public sealed class LocalHttpBridge : IAgentBridge, IDisposable
{
    private readonly ConcurrentQueue<DriverEvent> _events = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _token = "";
    private string _listenPrefix = "";

    /// <summary>事件队列上限，超出后丢弃最旧事件（防长跑内存无界增长）。</summary>
    public const int MaxQueuedEvents = 1000;

    public event EventHandler<MessageDelta>? NewMessage;
    public event EventHandler<DraftReply>? DraftProposed;

    /// <summary>当前监听端口，供示例客户端使用。</summary>
    public int Port { get; private set; }

    public string Token => _token;

    /// <summary>当前队列中待拉取的事件数（供测试/诊断）。</summary>
    public int QueuedEvents => _events.Count;

    public void Start(string token)
    {
        _token = token;
        Port = PickPort();
        _listenPrefix = $"http://127.0.0.1:{Port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_listenPrefix);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Publish(MessageDelta delta)
    {
        _events.Enqueue(DriverEvent.FromDelta(delta));
        // 有界队列：超出上限丢最旧，避免长跑内存无界增长。
        while (_events.Count > MaxQueuedEvents && _events.TryDequeue(out _)) { }
        NewMessage?.Invoke(this, delta);
    }

    public void RaiseDraft(DraftReply draft) => DraftProposed?.Invoke(this, draft);

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync().WaitAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { continue; }

            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var token = ctx.Request.Headers["X-Auth-Token"];
            if (token != _token)
            {
                Respond(ctx, 401, "{\"error\":\"unauthorized\"}");
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (path == "/events" && ctx.Request.HttpMethod == "GET")
            {
                var after = ParseSeq(ctx.Request.QueryString["after"]);
                var batch = _events.Where(e => Seq(e.EventId) > after).Take(50).ToList();
                Respond(ctx, 200, JsonSerializer.Serialize(batch));
            }
            else if (path == "/command" && ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = reader.ReadToEnd();
                var cmd = JsonSerializer.Deserialize<AgentCommand>(body);
                if (cmd is null || cmd.Type != "propose_draft")
                {
                    Respond(ctx, 400, "{\"error\":\"bad_command\"}");
                    return;
                }

                var conversation = cmd.Conversation is null
                    ? new ConversationKey("", "", null)
                    : new ConversationKey(cmd.Conversation.StableHint, cmd.Conversation.DisplayName, null);
                var draft = new DraftReply(cmd.DraftId, conversation, cmd.Text, cmd.EventId, DraftStatus.Proposed);
                RaiseDraft(draft);
                Respond(ctx, 200, "{\"ok\":true}");
            }
            else
            {
                Respond(ctx, 404, "{\"error\":\"not_found\"}");
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

    private static long ParseSeq(string? after) =>
        long.TryParse(after, out var v) ? v : 0;

    private static long Seq(string eventId)
    {
        var idx = eventId.LastIndexOf('_');
        return idx >= 0 && long.TryParse(eventId[(idx + 1)..], out var v) ? v : 0;
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
