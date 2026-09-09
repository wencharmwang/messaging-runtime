using System.IO;
using System.Windows;
using WeChatDriver.Core;

namespace WeChatDriver.App;

public partial class MainWindow : Window
{
    private readonly DriverHost _host;
    private string? _selectedConversation;
    private bool _busy;

    public MainWindow(DriverHost host)
    {
        InitializeComponent();
        _host = host;
        _host.MessageReceived += OnMessage;
        _host.StatusChanged += OnStatus;

        VersionText.Text = host.Session is null
            ? ""
            : $"微信 {host.Session.ClientVersion} · {host.Session.LocatorProfileId}";
        BridgeText.Text = host.BridgePort > 0
            ? $"Agent 接入: 127.0.0.1:{host.BridgePort} · 令牌 {host.BridgeToken}"
            : "Agent 桥未启动";

        // 初始刷新会话列表。
        RefreshConversations();
    }

    private void OnStatus(object? sender, string status) =>
        Dispatcher.Invoke(() => StatusText.Text = $"状态: {status}");

    private void OnMessage(object? sender, MessageDelta delta)
    {
        Dispatcher.Invoke(() =>
        {
            // Agent 自动推送的新消息：显示到消息区，便于查看。
            AppendMessage($"[新消息] {delta.Conversation.DisplayName}: {delta.Text}");
        });
    }

    private void AppendMessage(string text) =>
        // 消息之间加空行，避免长消息换行后与下一条粘在一起、难以阅读。
        MessagesBox.AppendText(text + "\r\n\r\n");

    // ---- 会话列表 ----

    private void RefreshConversations() => RunBusy(async () =>
    {
        var result = _host.Backend.EnumerateConversations();
        if (!result.Succeeded || result.Value is null)
        {
            SetResult($"读取会话列表失败: {result.Failure?.Code}");
            return;
        }
        await Dispatcher.InvokeAsync(() =>
        {
            ConversationList.Items.Clear();
            foreach (var c in result.Value)
            {
                var name = c.Key.DisplayName?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!ConversationList.Items.Contains(name))
                    ConversationList.Items.Add(name);
            }
            SetResult($"共 {ConversationList.Items.Count} 个会话");
        });
    });

    private void RefreshConversations_Click(object sender, RoutedEventArgs e) => RefreshConversations();

    private void ConversationList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedConversation = ConversationList.SelectedItem as string;
        ReadButton.IsEnabled = !string.IsNullOrWhiteSpace(_selectedConversation) && !_busy;
    }

    // ---- 读取消息 ----

    private void ReadMessages_Click(object sender, RoutedEventArgs e) => RunBusy(async () =>
    {
        if (string.IsNullOrWhiteSpace(_selectedConversation))
        {
            SetResult("请先在左侧选择一个会话");
            return;
        }

        SetResult($"正在切换到「{_selectedConversation}」并读取消息…");
        var switched = _host.Backend.SwitchConversation(_selectedConversation);
        if (!switched)
        {
            SetResult($"切换到「{_selectedConversation}」失败（未找到或未响应）");
            return;
        }

        // 每次读取都保留逐屏截图（放在 exe 同级 screenshots/ 目录，便于核对截图重叠效率）。
        var shotsDir = Path.Combine(AppContext.BaseDirectory, "screenshots", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        _host.Backend.EnableScreenshotCapture(shotsDir);

        var lines = _host.Backend.ReadChatHistory(out var stats, maxMessages: 200);
        await Dispatcher.InvokeAsync(() =>
        {
            MessagesBox.Clear();
            foreach (var line in lines)
                AppendMessage(line);
            SetResult($"读取完成：{lines.Count} 条 · 滚动 {stats.Scrolls} 屏 · OCR {stats.OcrCalls} 次\n逐屏截图：{shotsDir}");
            CopyButton.IsEnabled = lines.Count > 0;
        });
    });

    // ---- 复制消息 ----

    private void CopyMessages_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MessagesBox.Text)) return;
        System.Windows.Clipboard.SetText(MessagesBox.Text);
        SetResult("消息已复制到剪贴板，可粘贴给 AI 生成总结");
    }

    // ---- 写草稿 + 发送 ----

    private void WriteDraft_Click(object sender, RoutedEventArgs e) => RunBusy(() =>
    {
        if (string.IsNullOrWhiteSpace(DraftBox.Text))
        {
            SetResult("草稿为空");
            return;
        }
        var result = _host.Backend.WriteComposer(new ConversationKey("", _selectedConversation ?? "", null), DraftBox.Text);
        SetResult(result.Succeeded ? "草稿已写入微信输入框" : $"写入失败: {result.Failure?.Code} {result.Failure?.Message}");
        SendButton.IsEnabled = result.Succeeded;
    });

    private void Send_Click(object sender, RoutedEventArgs e) => RunBusy(() =>
    {
        var result = _host.Backend.SendMessage(new ConversationKey("", _selectedConversation ?? "", null));
        SetResult(result.Succeeded ? "已发送" : $"发送失败: {result.Failure?.Code} {result.Failure?.Message}");
        SendButton.IsEnabled = false;
    });

    // ---- 工具 ----

    private void SetResult(string text) =>
        Dispatcher.Invoke(() => ResultText.Text = text);

    private void RunBusy(Action action)
    {
        if (_busy) return;
        _busy = true;
        Dispatcher.Invoke(() => { ReadButton.IsEnabled = false; WriteButton.IsEnabled = false; SendButton.IsEnabled = false; });
        try
        {
            action();
        }
        catch (Exception ex)
        {
            SetResult($"异常: {ex.Message}");
        }
        finally
        {
            _busy = false;
            Dispatcher.Invoke(() =>
            {
                ReadButton.IsEnabled = !string.IsNullOrWhiteSpace(_selectedConversation);
                WriteButton.IsEnabled = true;
            });
        }
    }

    private void RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Dispatcher.Invoke(() => { ReadButton.IsEnabled = false; WriteButton.IsEnabled = false; SendButton.IsEnabled = false; });
        Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                SetResult($"异常: {ex.Message}");
            }
            finally
            {
                _busy = false;
                Dispatcher.Invoke(() =>
                {
                    ReadButton.IsEnabled = !string.IsNullOrWhiteSpace(_selectedConversation);
                    WriteButton.IsEnabled = true;
                });
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _host.MessageReceived -= OnMessage;
        _host.StatusChanged -= OnStatus;
        base.OnClosed(e);
    }
}
