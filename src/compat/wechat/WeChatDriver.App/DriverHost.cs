using System.Windows;
using WeChatDriver.AgentBridge;
using WeChatDriver.Automation;
using WeChatDriver.Core;
using WeChatDriver.Store;
using WeChatDriver.UIA;

namespace WeChatDriver.App;

/// <summary>驱动宿主：串起会话发现、增量读取、AgentBridge、存储与 UI。</summary>
public sealed class DriverHost : IDisposable
{
    private readonly OcrInputBackend _backend;
    private readonly InMemoryConversationIndex _index = new();
    private readonly SqliteStore _store;
    private readonly LocalHttpBridge _bridge = new();
    private readonly ActionExecutor _executor;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    public WeChatWindowSession? Session { get; private set; }
    public CapabilityMatrix? Capabilities { get; private set; }
    public SessionHealth Health { get; private set; } = SessionHealth.Unknown;

    /// <summary>后端自动化入口，供 GUI 主动读会话列表/读消息/写草稿/发送。</summary>
    public OcrInputBackend Backend => _backend;

    public event EventHandler<MessageDelta>? MessageReceived;
    public event EventHandler<string>? StatusChanged;

    public string BaseDir { get; }

    public DriverHost(string baseDir)
    {
        BaseDir = baseDir;
        _store = new SqliteStore(baseDir);
        _store.Initialize();
        _backend = new OcrInputBackend(expectedVersion: null);
        _executor = new ActionExecutor(_backend, _store);
        _bridge.NewMessage += (_, d) => System.Windows.Application.Current?.Dispatcher.Invoke(() => MessageReceived?.Invoke(this, d));
    }

    public int BridgePort => _bridge.Port;
    public string BridgeToken => _bridge.Token;

    /// <summary>导出脱敏 UIA 探测报告。用于 --probe-only 与启动诊断。</summary>
    public OperationResult<string> ProbeToJson()
    {
        var session = _backend.EstablishSession();
        if (!session.Succeeded || session.Value is null)
            return OperationResult<string>.FromFailure(session.Failure!);

        var window = Windows.WindowDiscovery.EnumerateTopLevelWindows()
            .FirstOrDefault(w => w.Handle == session.Value.WindowHandle);
        if (window is null)
            return OperationResult<string>.Fail(FailureCode.WindowNotFound, "未找到匹配的窗口句柄");

        var report = UiaProbe.Probe(session.Value, window);
        return OperationResult<string>.Ok(UiaProbe.ToJson(report));
    }

    public OperationResult<WeChatWindowSession> Initialize()
    {
        var session = _backend.EstablishSession();
        if (!session.Succeeded || session.Value is null)
        {
            Health = session.Failure?.Code == FailureCode.WeChatNotRunning
                ? SessionHealth.Blocked
                : SessionHealth.Degraded;
            StatusChanged?.Invoke(this, $"会话建立失败: {session.Failure?.Message}");
            return session;
        }

        Session = session.Value;
        Health = session.Value.Health;
        var caps = _backend.ProbeCapabilities();
        Capabilities = caps.Value;

        // AgentBridge：本地随机令牌，仅 127.0.0.1。
        var token = Guid.NewGuid().ToString("N");
        _bridge.Start(token);
        StatusChanged?.Invoke(this, $"Healthy · 桥端口 {_bridge.Port}");

        return session;
    }

    public void StartPolling(TimeSpan interval)
    {
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(interval);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(_cts.Token))
                {
                    PollOnce();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void PollOnce()
    {
        try
        {
            if (Session is null) return;

            var enumerate = _backend.EnumerateConversations();
            if (!enumerate.Succeeded || enumerate.Value is null) return;

            var changed = _index.Diff(enumerate.Value);
            foreach (var conv in changed)
            {
                var read = _backend.ReadLatestMessageText(conv.Key);
                if (!read.Succeeded || read.Value is null) continue;

                var fp = MessageFingerprint.Compute(conv.Key.StableHint, "in", MessageFingerprint.Normalize(read.Value), 0, DateTimeOffset.Now);
                if (_index.SeenFingerprint(fp)) continue;
                _index.MarkFingerprint(fp);

                var delta = new MessageDelta(
                    $"msg_{DateTimeOffset.Now:yyyyMMddHHmmssfff}",
                    conv.Key,
                    read.Value,
                    DateTimeOffset.Now,
                    MessageSource.Uia,
                    read.Confidence,
                    fp);

                _bridge.Publish(delta);
                MessageReceived?.Invoke(this, delta);
            }

            _index.Apply(enumerate.Value);
        }
        catch
        {
            // 轮询失败不致命。
        }
    }

    public sealed record DraftWriteOutcome(string OperationId, OperationResult Result);

    public DraftWriteOutcome WriteDraft(MessageDelta delta, string draftText)
    {
        var opId = Guid.NewGuid().ToString("N");
        var result = _executor.Execute(new ActionRequest(opId, delta.Conversation, draftText, delta.EventId, AllowSend: false));
        return new DraftWriteOutcome(opId, result);
    }

    public OperationResult ApproveAndSend(string operationId) => _executor.ApproveAndSend(operationId);

    public void Dispose()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _bridge.Stop();
    }
}
