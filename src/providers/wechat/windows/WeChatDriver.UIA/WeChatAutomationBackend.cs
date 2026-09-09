using System.Windows.Automation;
using WeChatDriver.Core;
using WeChatDriver.Windows;

namespace WeChatDriver.UIA;

/// <summary>基于 UIA 的微信自动化后端。主通道。</summary>
public sealed class WeChatAutomationBackend : IWindowAutomationBackend
{
    private readonly Version? _expectedVersion;
    private AutomationElement? _root;
    private WeChatWindowSession? _session;

    public WeChatAutomationBackend(Version? expectedVersion = null) => _expectedVersion = expectedVersion;

    public OperationResult<WeChatWindowSession> EstablishSession()
    {
        var result = WindowDiscovery.Discover(_expectedVersion);
        if (result.Succeeded && result.Value is not null)
        {
            _session = result.Value;
            _root = AutomationElement.FromHandle(result.Value.WindowHandle);
        }
        return result;
    }

    public WeChatWindowSession? Session => _session;

    public OperationResult<CapabilityMatrix> ProbeCapabilities()
    {
        if (_root is null || _session is null)
            return OperationResult<CapabilityMatrix>.Fail(FailureCode.SessionDisposed, "会话未建立");

        var nodes = new List<(string Ct, string? Name, string? ClassName, string[] Patterns)>();
        Walk(_root, 0, nodes);

        bool HasType(string ct) => nodes.Any(n => n.Ct == ct);
        bool HasEdit() => nodes.Any(n => n.Ct is "ControlType.Edit" or "ControlType.Document");
        var sendControl = nodes.Any(n => n.Patterns.Contains("Invoke"));

        var matrix = new CapabilityMatrix(_session.ClientVersion, _session.LocatorProfileId, new List<CapabilityEntry>
        {
            new("conversation_list", HasType("ControlType.List"), "List 控件"),
            new("chat_title", nodes.Any(n => n.Ct == "ControlType.Text" && !string.IsNullOrEmpty(n.Name)), "带 Name 的 Text"),
            new("composer", HasEdit(), "Edit/Document"),
            new("send_control", sendControl, "Invoke 控件"),
            new("message_area", nodes.Any(n => n.Ct is "ControlType.Text" or "ControlType.Document"), "消息区域")
        });
        return OperationResult<CapabilityMatrix>.Ok(matrix);
    }

    public OperationResult<IReadOnlyList<ConversationState>> EnumerateConversations()
    {
        if (_root is null)
            return OperationResult<IReadOnlyList<ConversationState>>.Fail(FailureCode.SessionDisposed, "会话未建立");

        var list = FindFirstByControlType(_root, ControlType.List);
        if (list is null)
            return OperationResult<IReadOnlyList<ConversationState>>.Fail(FailureCode.ConversationListMissing, "未找到会话列表");

        var items = list.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        var result = new List<ConversationState>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var name = SafeGet(item, AutomationElement.NameProperty) as string ?? $"#{i}";
            var aid = SafeGet(item, AutomationElement.AutomationIdProperty) as string;
            var stableHint = string.IsNullOrEmpty(aid) ? $"pos:{i}" : $"id:{aid}";
            var key = new ConversationKey(stableHint, name, null);
            result.Add(new ConversationState(key, null, null, null, DateTimeOffset.Now, true, false));
        }
        return OperationResult<IReadOnlyList<ConversationState>>.Ok(result);
    }

    public OperationResult<string?> ReadLatestMessageText(ConversationKey conversation)
    {
        if (_root is null)
            return OperationResult<string?>.Fail(FailureCode.SessionDisposed, "会话未建立");

        // 读取消息区域文本。优先取最底层 Text/Document。
        var texts = _root.FindAll(TreeScope.Descendants,
            new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));

        var candidates = new List<string>();
        for (var i = 0; i < texts.Count; i++)
        {
            var name = SafeGet(texts[i], AutomationElement.NameProperty) as string;
            if (!string.IsNullOrWhiteSpace(name)) candidates.Add(name);
        }

        var latest = candidates.LastOrDefault();
        if (latest is null)
            return OperationResult<string?>.Fail(FailureCode.MessageTextUnavailable, "消息区域无可读文本");

        return OperationResult<string?>.Ok(latest, 0.9);
    }

    public OperationResult WriteComposer(ConversationKey conversation, string text)
    {
        if (_root is null)
            return OperationResult.Fail(FailureCode.SessionDisposed, "会话未建立");

        var composer = FindFirstByControlType(_root, ControlType.Edit)
                       ?? FindFirstByControlType(_root, ControlType.Document);
        if (composer is null)
            return OperationResult.Fail(FailureCode.ComposerMissing, "未找到输入框");

        var valuePattern = composer.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
        if (valuePattern is not null && !valuePattern.Current.IsReadOnly)
        {
            try
            {
                valuePattern.SetValue(text);
                return OperationResult.Ok(0.95);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(FailureCode.WriteFailed, ex.Message);
            }
        }

        return OperationResult.Fail(FailureCode.ComposeUnsupported, "输入框不支持 ValuePattern 一次性写入");
    }

    public OperationResult<string?> ReadComposerText()
    {
        if (_root is null)
            return OperationResult<string?>.Fail(FailureCode.SessionDisposed, "会话未建立");

        var composer = FindFirstByControlType(_root, ControlType.Edit)
                       ?? FindFirstByControlType(_root, ControlType.Document);
        if (composer is null)
            return OperationResult<string?>.Fail(FailureCode.ComposerMissing, "未找到输入框");

        var valuePattern = composer.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
        return valuePattern is null
            ? OperationResult<string?>.Fail(FailureCode.ComposeUnsupported, "输入框不支持 ValuePattern")
            : OperationResult<string?>.Ok(valuePattern.Current.Value, 0.9);
    }

    public OperationResult SendMessage(ConversationKey conversation)
    {
        if (_root is null)
            return OperationResult.Fail(FailureCode.SessionDisposed, "会话未建立");

        var sendButton = FindFirstInvokable(_root);
        if (sendButton is null)
            return OperationResult.Fail(FailureCode.SendControlUnknown, "未找到发送控件，禁止猜测发送键");

        var invoke = sendButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
        try
        {
            invoke?.Invoke();
            return OperationResult.Ok(0.85);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(FailureCode.SendFailed, ex.Message);
        }
    }

    public OperationResult<bool> VerifySent(ConversationKey conversation, string expectedFingerprint)
    {
        var composer = ReadComposerText();
        if (!composer.Succeeded)
            return OperationResult<bool>.FromFailure(composer.Failure!);

        // 发送成功的最强信号：输入框已清空。
        var empty = string.IsNullOrWhiteSpace(composer.Value);
        if (empty) return OperationResult<bool>.Ok(true, 0.9);

        return OperationResult<bool>.Fail(FailureCode.SendUnverified, "发送后输入框未清空，无法验证发送结果");
    }

    // ---- helpers ----

    private static void Walk(AutomationElement el, int depth, List<(string, string?, string?, string[])> acc, int maxDepth = 10, int maxNodes = 300)
    {
        if (depth > maxDepth || acc.Count >= maxNodes) return;
        var ct = SafeGet(el, AutomationElement.ControlTypeProperty) as ControlType;
        var name = SafeGet(el, AutomationElement.NameProperty) as string;
        var cls = SafeGet(el, AutomationElement.ClassNameProperty) as string;
        var patterns = new List<string>();
        foreach (var p in new[] { ValuePattern.Pattern, InvokePattern.Pattern, TextPattern.Pattern, ScrollPattern.Pattern })
            if (Supports(el, p)) patterns.Add(p.ProgrammaticName);
        acc.Add((ct?.ProgrammaticName ?? "Unknown", name, cls, patterns.ToArray()));
        var children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
        for (var i = 0; i < children.Count && acc.Count < maxNodes; i++)
            Walk(children[i], depth + 1, acc, maxDepth, maxNodes);
    }

    private static AutomationElement? FindFirstByControlType(AutomationElement root, ControlType type) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type));

    private static AutomationElement? FindFirstInvokable(AutomationElement root)
    {
        var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        for (var i = 0; i < all.Count; i++)
        {
            if (Supports(all[i], InvokePattern.Pattern))
                return all[i];
        }
        return null;
    }

    private static bool Supports(AutomationElement el, AutomationPattern pattern)
    {
        try { el.GetCurrentPattern(pattern); return true; }
        catch { return false; }
    }

    private static object? SafeGet(AutomationElement el, AutomationProperty prop)
    {
        try { return el.GetCurrentPropertyValue(prop); }
        catch { return null; }
    }
}
