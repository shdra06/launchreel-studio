using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Studio.Host;

/// <summary>
/// Operates a mirrored app from Studio's 3D view without touching the user's real mouse, keyboard focus or window
/// placement. A click on the 3D panel is translated to the exact pixel of the real window (or of one of its pop-ups)
/// and delivered as the window messages a real click would produce. Nothing is ever sent to any other process.
/// Keyboard text relies on the app's own TranslateMessage, so layouts and Shift/Caps follow the real keyboard.
/// </summary>
public sealed class WindowInput
{
    private const uint MouseMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202, LeftDouble = 0x0203, RightDown = 0x0204, RightUp = 0x0205,
        MiddleDown = 0x0207, MiddleUp = 0x0208, Wheel = 0x020A, KeyDown = 0x0100, KeyUp = 0x0101, SysKeyDown = 0x0104, SysKeyUp = 0x0105;
    private nint captured;      // child that received the button-down, like SetCapture
    private nint keyboardTarget;// last child clicked: where typing goes
    private long lastDown;
    private (int X, int Y) lastDownAt;

    /// <summary>Screen origin of window-capture pixels: the visible frame, without the invisible resize borders.</summary>
    public static (int X, int Y) Origin(nint window)
    {
        if (DwmGetWindowAttribute(window, 9, out var frame, Marshal.SizeOf<NativeRect>()) == 0) return (frame.Left, frame.Top);
        GetWindowRect(window, out var rect);
        return (rect.Left, rect.Top);
    }

    /// <summary>Delivers a pointer action at (x, y) pixels of the mirrored picture whose top-left is <paramref name="origin"/> on screen.</summary>
    public void Pointer(uint pid, (int X, int Y) origin, IReadOnlyList<nint> windows, JsonObject data)
    {
        var action = data["action"]?.GetValue<string>() ?? "";
        var screen = (X: origin.X + (int)Math.Round(data["x"]!.GetValue<double>()), Y: origin.Y + (int)Math.Round(data["y"]!.GetValue<double>()));
        var target = captured != 0 && IsWindow(captured) && action is "pointerMove" or "pointerUp" ? captured : Deepest(windows, screen);
        if (target == 0) return;
        if (!SameProcess(target, pid)) return; // Defence in depth: input never leaves the mirrored app.
        var client = new NativePoint { X = screen.X, Y = screen.Y };
        ScreenToClient(target, ref client);
        var lParam = (nint)((client.Y << 16) | (client.X & 0xFFFF));
        var button = data["button"]?.GetValue<int>() ?? 0;
        uint keys = Modifiers();
        switch (action)
        {
            case "pointerMove":
                if (captured != 0) keys |= 0x0001;
                Post(target, MouseMove, keys, lParam);
                break;
            case "pointerDown":
            {
                captured = target;
                if (button == 0) keyboardTarget = target;
                var now = Environment.TickCount64;
                var isDouble = button == 0 && now - lastDown <= GetDoubleClickTime() && Math.Abs(screen.X - lastDownAt.X) <= 4 && Math.Abs(screen.Y - lastDownAt.Y) <= 4;
                lastDown = isDouble ? 0 : now; lastDownAt = screen;
                Post(target, MouseMove, keys, lParam);
                Post(target, button switch { 2 => RightDown, 1 => MiddleDown, _ => isDouble ? LeftDouble : LeftDown }, keys | button switch { 2 => 0x0002u, 1 => 0x0010u, _ => 0x0001u }, lParam);
                break;
            }
            case "pointerUp":
                Post(target, button switch { 2 => RightUp, 1 => MiddleUp, _ => LeftUp }, keys, lParam);
                captured = 0;
                break;
            case "scroll":
            {
                var delta = -Math.Sign(data["delta"]?.GetValue<double>() ?? 0) * 120;
                if (delta == 0) break;
                // Wheel messages carry screen coordinates.
                Post(target, Wheel, (uint)((delta & 0xFFFF) << 16) | keys, (nint)((screen.Y << 16) | (screen.X & 0xFFFF)));
                break;
            }
        }
    }

    /// <summary>Key presses go to the control the user last clicked in the mirror (or the app's own focus).</summary>
    public void Key(uint pid, nint window, JsonObject data)
    {
        var vk = data["vk"]?.GetValue<int>() ?? 0;
        if (vk is <= 0 or > 254 || vk is 0x5B or 0x5C) return; // no Windows key
        var target = keyboardTarget != 0 && IsWindow(keyboardTarget) && SameProcess(keyboardTarget, pid) ? keyboardTarget : Focused(window, pid);
        if (target == 0) return;
        var down = data["type"]?.GetValue<string>() != "up";
        var alt = data["alt"]?.GetValue<bool>() == true;
        var held = new List<ushort> { (ushort)vk };
        if (alt) held.Add(0x12);
        if ((GetKeyState(0x11) & 0x8000) != 0) held.Add(0x11);
        if (DemoScript.ClosesOrSwitches(held)) return;
        var scan = MapVirtualKey((uint)vk, 0);
        var lParam = (nint)(1 | (scan << 16) | (alt ? 1 << 29 : 0) | (down ? 0 : (3 << 30)));
        Post(target, alt ? (down ? SysKeyDown : SysKeyUp) : (down ? KeyDown : KeyUp), (uint)vk, lParam);
    }

    private static nint Focused(nint window, uint pid)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var thread = GetWindowThreadProcessId(window, out _);
        return GetGUIThreadInfo(thread, ref info) && info.Focus != 0 && SameProcess(info.Focus, pid) ? info.Focus : window;
    }

    /// <summary>The innermost visible child under a screen point, searching pop-ups first (they sit on top).</summary>
    private static nint Deepest(IReadOnlyList<nint> windows, (int X, int Y) screen)
    {
        foreach (var top in windows)
        {
            if (!GetWindowRect(top, out var r) || screen.X < r.Left || screen.X >= r.Right || screen.Y < r.Top || screen.Y >= r.Bottom) continue;
            var current = top;
            for (var depth = 0; depth < 32; depth++)
            {
                var p = new NativePoint { X = screen.X, Y = screen.Y };
                ScreenToClient(current, ref p);
                var child = ChildWindowFromPointEx(current, p, 0x0001 | 0x0002 | 0x0004);
                if (child == 0 || child == current) break;
                current = child;
            }
            return current;
        }
        return 0;
    }

    private static bool SameProcess(nint window, uint pid) { GetWindowThreadProcessId(window, out var p); return p == pid; }
    private static uint Modifiers() => ((GetKeyState(0x10) & 0x8000) != 0 ? 0x0004u : 0) | ((GetKeyState(0x11) & 0x8000) != 0 ? 0x0008u : 0);
    private static void Post(nint window, uint message, uint wParam, nint lParam) => PostMessage(window, message, (nint)wParam, lParam);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo { public int Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public NativeRect CaretRect; }
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint parent, NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect value, int size);
}
