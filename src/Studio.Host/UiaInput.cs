using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Accessibility;

namespace Studio.Host;

/// <summary>
/// Live 3D input for apps built on WPF, WinUI or UWP. Those frameworks hit-test clicks at the real cursor and only
/// accept keys while their window is active, so window messages cannot drive them without disturbing the user.
/// Instead a click on the mirror is hit-tested against the app's own UI Automation tree and performed as the
/// control's own action (press, toggle, select, expand); text goes into the clicked field through its value. The
/// actions go through the control's accessible object: UI Automation's own Invoke and SetValue activate the app's
/// window, the accessible default action and value do not. The wheel scrolls the nearest scrollable area. None of this moves the cursor, changes focus or moves the window.
/// </summary>
public sealed class UiaInput
{
    private (IAccessible Field, object Child)? typingInto;
    private (int X, int Y) downAt;

    /// <summary>WPF, WinUI and UWP top-level windows, which ignore posted mouse and key messages while inactive.</summary>
    public static bool Needed(nint window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        var cls = name.ToString();
        return cls.StartsWith("HwndWrapper[", StringComparison.Ordinal) || cls is "WinUIDesktopWin32WindowClass" or "ApplicationFrameWindow" or "Windows.UI.Core.CoreWindow";
    }

    public void Pointer(IReadOnlyList<nint> windows, (int X, int Y) screen, JsonObject data)
    {
        var action = data["action"]?.GetValue<string>();
        if (action == "pointerDown") { downAt = screen; return; }
        if (action == "scroll") { Scroll(windows, screen, data["delta"]?.GetValue<double>() ?? 0); return; }
        // A click is a press and release in nearly the same place; drags have no automation equivalent.
        if (action != "pointerUp" || Math.Abs(screen.X - downAt.X) > 6 || Math.Abs(screen.Y - downAt.Y) > 6 || (data["button"]?.GetValue<int>() ?? 0) != 0) return;
        var target = HitTest(windows, screen);
        for (var e = target; e is not null; e = TreeWalker.ControlViewWalker.GetParent(e))
        {
            try
            {
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var v) && !((ValuePattern)v).Current.IsReadOnly) { typingInto = Accessible(windows, screen, e); return; }
                if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _) && !e.TryGetCurrentPattern(InvokePattern.Pattern, out _))
                { if (Accessible(windows, screen, e) is var (item, id)) item.accSelect(2, id); return; } // SELFLAG_TAKESELECTION, no focus
                if (e.TryGetCurrentPattern(InvokePattern.Pattern, out _) || e.TryGetCurrentPattern(TogglePattern.Pattern, out _) || e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _))
                { if (Accessible(windows, screen, e) is var (control, id)) control.accDoDefaultAction(id); return; }
            }
            catch (Exception error) when (error is InvalidOperationException or ElementNotAvailableException or COMException or InvalidCastException) { return; }
        }
    }

    /// <summary>Typing into the clicked field: characters follow the real keyboard layout and Shift state.</summary>
    public void Key(JsonObject data)
    {
        if (data["type"]?.GetValue<string>() == "up" || typingInto is null) return;
        var vk = data["vk"]?.GetValue<int>() ?? 0;
        var (field, child) = typingInto.Value;
        try
        {
            var text = field.get_accValue(child) ?? "";
            if (vk == 0x08) { if (text.Length > 0) field.set_accValue(child, text[..^1]); return; }
            var ch = Character((uint)vk);
            if (ch is not null) field.set_accValue(child, text + ch);
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { typingInto = null; }
    }

    private static string? Character(uint vk)
    {
        var state = new byte[256];
        foreach (var k in new[] { 0x10, 0x14, 0x11, 0x12 }) state[k] = (byte)((GetKeyState(k) & 0x8000) != 0 || (k == 0x14 && (GetKeyState(k) & 1) != 0) ? 0x81 : 0);
        if ((state[0x11] & 0x80) != 0 && (state[0x12] & 0x80) == 0) return null; // Ctrl shortcuts are not text
        var buffer = new StringBuilder(4);
        var n = ToUnicode(vk, MapVirtualKey(vk, 0), state, buffer, buffer.Capacity, 0);
        return n > 0 && !char.IsControl(buffer[0]) ? buffer.ToString(0, n) : null;
    }

    private static void Scroll(IReadOnlyList<nint> windows, (int X, int Y) screen, double delta)
    {
        for (var e = HitTest(windows, screen); e is not null; e = TreeWalker.ControlViewWalker.GetParent(e))
        {
            try
            {
                if (e.TryGetCurrentPattern(ScrollPattern.Pattern, out var s) && ((ScrollPattern)s).Current.VerticallyScrollable)
                { ((ScrollPattern)s).ScrollVertical(delta > 0 ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement); return; }
            }
            catch (Exception error) when (error is InvalidOperationException or ElementNotAvailableException) { return; }
        }
    }

    /// <summary>
    /// The accessible object of an automation element: the app hit-tests the point itself (not what is on screen
    /// there), then the result is walked up to the object whose location matches the element.
    /// </summary>
    private static (IAccessible, object)? Accessible(IReadOnlyList<nint> windows, (int X, int Y) screen, AutomationElement element)
    {
        var want = element.Current.BoundingRectangle;
        bool Matches(IAccessible a, object child)
        {
            a.accLocation(out var l, out var t, out var w, out var h, child);
            return Math.Abs(l - want.Left) <= 2 && Math.Abs(t - want.Top) <= 2 && Math.Abs(w - want.Width) <= 2 && Math.Abs(h - want.Height) <= 2;
        }
        foreach (var top in windows)
        {
            try
            {
                var iid = typeof(IAccessible).GUID;
                if (AccessibleObjectFromWindow(top, -4, ref iid, out var found) != 0 || found is not IAccessible acc) continue;
                object child = 0;
                for (var depth = 0; depth < 40; depth++)
                {
                    var hit = acc.accHitTest(screen.X, screen.Y);
                    if (hit is IAccessible inner && !ReferenceEquals(inner, acc)) { acc = inner; continue; }
                    if (hit is int id) child = id;
                    break;
                }
                for (var depth = 0; depth < 40; depth++)
                {
                    if (Matches(acc, child)) return (acc, child);
                    if (child is int id && id != 0) { child = 0; continue; }
                    if (acc.accParent is not IAccessible parent || ReferenceEquals(parent, acc)) break;
                    acc = parent;
                }
            }
            catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// The innermost element whose bounds contain the point, found by walking the tree. AutomationElement.FromPoint
    /// would ask what is on screen there, which is Studio, because the mirrored app sits behind it.
    /// </summary>
    private static AutomationElement? HitTest(IReadOnlyList<nint> windows, (int X, int Y) screen)
    {
        var point = new System.Windows.Point(screen.X, screen.Y);
        foreach (var top in windows)
        {
            AutomationElement root;
            try { root = AutomationElement.FromHandle(top); if (!root.Current.BoundingRectangle.Contains(point)) continue; }
            catch (ElementNotAvailableException) { continue; }
            var current = root;
            for (var depth = 0; depth < 40; depth++)
            {
                AutomationElement? next = null;
                try
                {
                    for (var child = TreeWalker.ControlViewWalker.GetFirstChild(current); child is not null; child = TreeWalker.ControlViewWalker.GetNextSibling(child))
                    {
                        var info = child.Current;
                        if (!info.IsOffscreen && info.BoundingRectangle.Contains(point)) next = child; // later siblings draw on top
                    }
                }
                catch (ElementNotAvailableException) { }
                if (next is null) break;
                current = next;
            }
            return current;
        }
        return null;
    }

    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(nint window, int objectId, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object accessible);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicode(uint vk, uint scan, byte[] state, StringBuilder buffer, int size, uint flags);
}
