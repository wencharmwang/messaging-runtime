using System.Drawing;
using System.Windows.Automation;
using WeChatDriver.Windows;

namespace WeChatDriver.UIA;

public sealed record NativeComposerRead(bool Available, string? Text, string Source);

/// <summary>Read a uniquely bounded editor without focusing it or sending input.</summary>
public sealed class NativeComposerReader
{
    private static readonly object Gate = new();
    private static Task<NativeComposerRead>? _pending;
    private (nint Hwnd, int Pid, Rectangle Region)? _unavailableTarget;
    private DateTime _retryAfter;

    public NativeComposerRead Read(nint hwnd, int processId, Rectangle composer, bool retryUnavailable = false)
    {
        var target = (hwnd, processId, composer);
        if (!retryUnavailable && _unavailableTarget == target && DateTime.UtcNow < _retryAfter)
            return new(false, null, "uia_unavailable_cached");
        Task<NativeComposerRead> work;
        lock (Gate)
        {
            // At most one outstanding COM call, even if a UIA provider stops responding.
            if (_pending is { IsCompleted: false }) return new(false, null, "uia_busy");
            work = _pending = Task.Run(() => ReadCore(hwnd, processId, composer));
        }
        var result = work.Wait(TimeSpan.FromMilliseconds(1000))
            ? work.Result : new NativeComposerRead(false, null, "uia_timeout");
        if (!result.Available)
        {
            _unavailableTarget = target;
            _retryAfter = DateTime.UtcNow.AddSeconds(60);
        }
        return result;
    }

    public static bool IsComposerBounds(Rectangle composer, Rectangle editor)
    {
        var allowed = composer;
        allowed.Inflate(8, 8);
        return editor.Width >= 100 && editor.Height >= 10 && allowed.Contains(editor);
    }

    private static NativeComposerRead ReadCore(nint hwnd, int processId, Rectangle composer)
    {
        try
        {
            using var dpi = new DpiAwarenessScope();
            var window = WindowDiscovery.GetWindowBounds(hwnd);
            var screen = composer;
            screen.Offset(window.X, window.Y);
            var root = AutomationElement.FromHandle(hwnd);
            if (root.Current.ProcessId != processId) return new(false, null, "uia_target_changed");
            var editors = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));
            var candidates = new List<AutomationElement>();
            foreach (AutomationElement editor in editors)
            {
                var info = editor.Current;
                var b = info.BoundingRectangle;
                if (info.ProcessId != processId || !info.IsEnabled || b.IsEmpty) continue;
                var rect = Rectangle.FromLTRB((int)Math.Floor(b.Left), (int)Math.Floor(b.Top),
                    (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom));
                if (IsComposerBounds(screen, rect)) candidates.Add(editor);
            }
            if (candidates.Count != 1) return new(false, null, "uia_editor_not_unique");
            var target = candidates[0];
            string text;
            string source;
            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            {
                text = ((ValuePattern)value).Current.Value;
                source = "uia_value";
            }
            else if (target.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            {
                text = ((TextPattern)pattern).DocumentRange.GetText(10001);
                source = "uia_text";
            }
            else return new(false, null, "uia_text_unsupported");
            if (text.Length > 10000 || WindowDiscovery.GetWindowBounds(hwnd) != window
                || root.Current.ProcessId != processId) return new(false, null, "uia_read_unverified");
            return new(true, text, source);
        }
        catch (Exception) { return new(false, null, "uia_unavailable"); }
    }
}
