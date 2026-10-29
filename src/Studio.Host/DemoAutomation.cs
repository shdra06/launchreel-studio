using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Studio.Host;

public sealed record UiElementInfo(string Name, string Type, double X, double Y, double Width, double Height);

/// <summary>
/// Performs a validated demo on the connected app with real mouse and keyboard input, so the app runs its own code
/// exactly as it would for a person. Controls are found by name through UI Automation, which WPF, WinUI and most
/// desktop frameworks expose. Input only ever lands on the connected app's own windows: a step that would click
/// anything else stops the demo, and so does moving the mouse or pressing Esc.
/// </summary>
public sealed class DemoAutomation(nint window)
{
    private const int MaxElements = 400;
    private static readonly HashSet<string> Interesting = ["Button", "ListItem", "TabItem", "MenuItem", "Hyperlink", "TreeItem", "CheckBox", "RadioButton", "ComboBox", "Edit", "Text", "SplitButton", "DataItem", "Document"];
    private (int X, int Y)? expectedCursor;

    /// <summary>Named, visible controls of the window with their centres as fractions of the window. Read-only.</summary>
    public List<UiElementInfo> Elements()
    {
        var bounds = WindowBounds();
        var result = new List<UiElementInfo>();
        foreach (var (name, type, rect) in Walk())
        {
            if (type == "Text" && name.Length > 48) continue;
            result.Add(new UiElementInfo(name, type, (rect.Left + rect.Width / 2 - bounds.Left) / bounds.Width, (rect.Top + rect.Height / 2 - bounds.Top) / bounds.Height, rect.Width / bounds.Width, rect.Height / bounds.Height));
            if (result.Count >= 300) break;
        }
        return result;
    }

    private IEnumerable<(string Name, string Type, System.Windows.Rect Rect)> Walk()
    {
        AutomationElement root;
        try { root = AutomationElement.FromHandle(window); }
        catch (ElementNotAvailableException) { yield break; }
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>([(root, 0)]);
        var seen = 0;
        while (queue.Count > 0 && seen < MaxElements * 4)
        {
            var (element, depth) = queue.Dequeue();
            seen++;
            string name, type; System.Windows.Rect rect; bool offscreen;
            try
            {
                var info = element.Current;
                name = info.Name?.Trim() ?? ""; type = info.ControlType.ProgrammaticName.Replace("ControlType.", ""); rect = info.BoundingRectangle; offscreen = info.IsOffscreen;
            }
            catch (ElementNotAvailableException) { continue; }
            if (offscreen && depth > 0) continue;
            // The window's own caption buttons (close, minimize, maximize) are never offered to a demo.
            if (type == "TitleBar" || (type == "Button" && name is "Close" or "Minimize" or "Maximize" or "Restore" or "Minimise" or "Maximise")) continue;
            if (depth > 0 && name.Length > 0 && Interesting.Contains(type) && !rect.IsEmpty && rect.Width >= 4 && rect.Height >= 4) yield return (name, type, rect);
            if (depth >= 30) continue;
            AutomationElement? child = null;
            try { child = walker.GetFirstChild(element); } catch (ElementNotAvailableException) { }
            while (child is not null)
            {
                queue.Enqueue((child, depth + 1));
                try { child = walker.GetNextSibling(child); } catch (ElementNotAvailableException) { child = null; }
            }
        }
    }

    public async Task Run(DemoPlan plan, Action<int, string> progress, CancellationToken token)
    {
        if (!IsWindow(window) || !IsWindowVisible(window)) throw new InvalidOperationException("The app window is not open.");
        if (IsIconic(window)) { ShowWindow(window, 9); await Task.Delay(400, token); }
        SetForegroundWindow(window);
        await Pause(plan.Quick ? 250 : 600, token);
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            progress(i, DemoScript.Describe(step));
            switch (step.Action)
            {
                case DemoAction.Wait: await Pause((int)(step.Seconds * 1000), token); break;
                case DemoAction.Type: GuardKeyboard(); await TypeText(step.Text!, plan.Quick, token); break;
                case DemoAction.Keys: GuardKeyboard(); Press(step.Keys!); break;
                case DemoAction.Scroll:
                {
                    var (x, y) = step.Target is null && step.X is null ? Centre() : await Locate(step, token);
                    await MoveTo(x, y, plan.Quick, token);
                    for (var n = 0; n < Math.Abs(step.Amount); n++) { Mouse(0x0800, step.Amount > 0 ? -120 : 120); await Pause(plan.Quick ? 40 : 110, token); }
                    break;
                }
                default:
                {
                    var (x, y) = await Locate(step, token);
                    await MoveTo(x, y, plan.Quick, token);
                    if (step.Action == DemoAction.Hover) { await Pause(500, token); break; }
                    Guard(x, y);
                    var (down, up) = step.Action == DemoAction.RightClick ? (0x0008u, 0x0010u) : (0x0002u, 0x0004u);
                    for (var n = 0; n < (step.Action == DemoAction.DoubleClick ? 2 : 1); n++) { Mouse(down); try { await Task.Delay(55, CancellationToken.None); } finally { Mouse(up); } await Task.Delay(80, token); }
                    break;
                }
            }
            await Pause(plan.Quick ? 120 : 380, token);
        }
    }

    /// <summary>Finds the step's target in the live UI. Waits briefly, because apps often animate new screens in.</summary>
    private async Task<(int X, int Y)> Locate(DemoStep step, CancellationToken token)
    {
        if (step.Target is null)
        {
            var b = WindowBounds();
            return ((int)(b.Left + step.X!.Value * b.Width), (int)(b.Top + step.Y!.Value * b.Height));
        }
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var candidates = await Task.Run(() => Walk().Take(MaxElements).ToList(), token);
            var index = DemoScript.Match(step.Target, candidates.Select(c => (c.Name, c.Type)).ToList());
            if (index >= 0) { var r = candidates[index].Rect; return ((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2)); }
            await Pause(250, token);
        }
        throw new InvalidOperationException($"Could not find “{step.Target}” in the app. Check the name in the prompt's control list.");
    }

    private async Task MoveTo(int x, int y, bool quick, CancellationToken token)
    {
        CheckUser();
        GetCursorPos(out var from);
        var distance = Math.Sqrt(Math.Pow(x - from.X, 2) + Math.Pow(y - from.Y, 2));
        var seconds = quick ? .12 : Math.Min(.9, .28 + distance / 2600);
        foreach (var (px, py) in DemoScript.CursorPath(from.X, from.Y, x, y, (int)(seconds * 120)))
        {
            SetCursorPos(px, py);
            expectedCursor = (px, py);
            await Task.Delay(8, token);
            CheckUser();
        }
    }

    /// <summary>Clicks only ever land on the connected app, including its own menus and popups.</summary>
    private void Guard(int x, int y)
    {
        GetWindowThreadProcessId(window, out var appPid);
        var hit = GetAncestor(WindowFromPoint(new NativePoint { X = x, Y = y }), 2);
        GetWindowThreadProcessId(hit, out var hitPid);
        if (hitPid != appPid) throw new InvalidOperationException("Something else is covering the app at that spot, so the demo stopped instead of clicking it.");
    }

    /// <summary>Keys and text only ever go to the app being demonstrated, never to whatever else has focus.</summary>
    private void GuardKeyboard()
    {
        GetWindowThreadProcessId(window, out var appPid);
        GetWindowThreadProcessId(GetForegroundWindow(), out var focusedPid);
        if (focusedPid != appPid) throw new InvalidOperationException("The app does not have keyboard focus, so the demo stopped instead of typing into another app. Start with a click inside the app.");
    }

    private async Task TypeText(string text, bool quick, CancellationToken token)
    {
        for (var i = 0; i < text.Length; i++)
        {
            CheckUser();
            if (text[i] == '\n') Press([0x0D]);
            else { Key(0, text[i], 0x0004); Key(0, text[i], 0x0004 | 0x0002); }
            // A steady, slightly uneven rhythm reads as typing rather than pasting.
            await Task.Delay(quick ? 12 : 45 + (i * 37 % 40), token);
        }
    }

    private static void Press(IReadOnlyList<ushort> keys)
    {
        var pressed = new List<ushort>();
        try { foreach (var key in keys) { Key(key, 0, 0); pressed.Add(key); } }
        finally { foreach (var key in Enumerable.Reverse(pressed)) { try { Key(key, 0, 0x0002); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); } } }
    }

    /// <summary>Waits in short slices so Esc or a mouse movement stops the demo promptly.</summary>
    private async Task Pause(int milliseconds, CancellationToken token)
    {
        for (var waited = 0; waited < milliseconds; waited += 50) { CheckUser(); await Task.Delay(Math.Min(50, milliseconds - waited), token); }
    }

    private void CheckUser()
    {
        if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) throw new OperationCanceledException("You pressed Esc, so the demo stopped.");
        if (expectedCursor is (int ex, int ey) && GetCursorPos(out var now) && Math.Abs(now.X - ex) + Math.Abs(now.Y - ey) > 14)
            throw new OperationCanceledException("You moved the mouse, so the demo stopped.");
        if (!IsWindow(window) || !IsWindowVisible(window)) throw new InvalidOperationException("The app window closed during the demo.");
    }

    private (int X, int Y) Centre() { var b = WindowBounds(); return ((int)(b.Left + b.Width / 2), (int)(b.Top + b.Height / 2)); }
    private (double Left, double Top, double Width, double Height) WindowBounds()
    {
        if (!AppWindows.GetWindowRect(window, out var r)) throw new InvalidOperationException("The app window is not available.");
        return (r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }

    private static void Mouse(uint flags, int data = 0) => Send(new Input { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = flags, Data = data } } });
    private static void Key(ushort vk, ushort unicode, uint flags) => Send(new Input { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, Scan = unicode, Flags = flags } } });
    private static void Send(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new InvalidOperationException("Windows blocked the demo input. Studio and the app must run at the same permission level.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public int Data; public uint Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Union; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
}
