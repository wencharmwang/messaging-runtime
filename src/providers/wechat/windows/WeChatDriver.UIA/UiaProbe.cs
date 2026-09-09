using System.Text.Json;
using System.Windows.Automation;
using WeChatDriver.Core;
using WeChatDriver.Windows;

namespace WeChatDriver.UIA;

/// <summary>UIA 探测：输出脱敏后的能力矩阵与树快照。</summary>
public sealed class UiaProbe
{
    private const int MaxDepth = 12;
    private const int MaxNodes = 400;

    public sealed record ProbeReport(
        string ClientVersion,
        string LocatorProfileId,
        int Dpi,
        DiscoveredWindow Window,
        CapabilityMatrix Capabilities,
        IReadOnlyList<UiaNodeDto> Tree);

    public sealed record UiaNodeDto(
        string ControlType,
        string MaskedName,
        string? AutomationId,
        string? ClassName,
        int X, int Y, int Width, int Height,
        IReadOnlyList<string> Patterns);

    public static ProbeReport Probe(WeChatWindowSession session, DiscoveredWindow window)
    {
        var root = AutomationElement.FromHandle(session.WindowHandle);
        if (root is null)
            throw new InvalidOperationException("AutomationElement.FromHandle 返回 null");

        var nodes = new List<UiaNodeDto>();
        Walk(root, 0, nodes);

        var caps = DetectCapabilities(session.ClientVersion, session.LocatorProfileId, nodes);
        return new ProbeReport(session.ClientVersion, session.LocatorProfileId, WindowDiscovery.GetDpi(window.Handle), window, caps, nodes);
    }

    private static void Walk(AutomationElement el, int depth, List<UiaNodeDto> nodes)
    {
        if (nodes.Count >= MaxNodes || depth > MaxDepth) return;

        var ct = SafeGet(el, AutomationElement.ControlTypeProperty) as ControlType;
        var name = SafeGet(el, AutomationElement.NameProperty) as string;
        var aid = SafeGet(el, AutomationElement.AutomationIdProperty) as string;
        var cls = SafeGet(el, AutomationElement.ClassNameProperty) as string;
        var rect = el.Current.BoundingRectangle;

        var patterns = new List<string>();
        foreach (var (pattern, label) in PatternProbes)
        {
            if (Supports(el, pattern)) patterns.Add(label);
        }

        nodes.Add(new UiaNodeDto(
            ct?.ProgrammaticName ?? "Unknown",
            UiaSanitizer.MaskName(name),
            aid,
            cls,
            (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height,
            patterns));

        var children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
        for (var i = 0; i < children.Count && nodes.Count < MaxNodes; i++)
            Walk(children[i], depth + 1, nodes);
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

    private static readonly (AutomationPattern Pattern, string Label)[] PatternProbes =
    {
        (ValuePattern.Pattern, "Value"),
        (InvokePattern.Pattern, "Invoke"),
        (SelectionItemPattern.Pattern, "SelectionItem"),
        (ScrollPattern.Pattern, "Scroll"),
        (TextPattern.Pattern, "Text"),
        (ExpandCollapsePattern.Pattern, "ExpandCollapse"),
        (TogglePattern.Pattern, "Toggle")
    };

    private static CapabilityMatrix DetectCapabilities(string version, string profileId, List<UiaNodeDto> nodes)
    {
        bool HasControlType(string ct) => nodes.Any(n => n.ControlType == ct);
        bool HasEdit() => nodes.Any(n => n.ControlType is "ControlType.Edit" or "ControlType.Document");

        var entries = new List<CapabilityEntry>
        {
            new("conversation_list", HasControlType("ControlType.List"), "存在 List 控件"),
            new("chat_title", nodes.Any(n => n.ControlType == "ControlType.Text" && n.MaskedName.Length > 0), "存在带 Name 的 Text"),
            new("composer", HasEdit(), "存在 Edit/Document 控件"),
            new("send_control", nodes.Any(n => n.Patterns.Contains("Invoke") && n.MaskedName.Length > 0), "存在可 Invoke 控件"),
            new("message_area", nodes.Any(n => n.ControlType is "ControlType.Text" or "ControlType.Document"), "消息区域候选")
        };
        return new CapabilityMatrix(version, profileId, entries);
    }

    public static string ToJson(ProbeReport report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
}
