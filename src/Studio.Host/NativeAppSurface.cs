using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using static Studio.Host.AppWindows;

namespace Studio.Host;

/// <summary>
/// Places a real, separately running application window over Studio's reserved App view rectangle.
///
/// Stability rules, learned the hard way:
/// - The app's window is never owned by or parented to Studio. A cross-process owner or parent makes Windows attach
///   the two apps' input queues; with an app that installs low-level keyboard or mouse hooks (FlyShelf does), any
///   stall in either app then freezes input for the whole PC. Studio keeps the window above itself by z-order only.
/// - Studio never sends the app a synchronous window call. Moves, raises and shows are asynchronous, so a busy or
///   hung app can never freeze Studio, and Studio can never hold up the app.
/// - Studio never installs hooks into the app. Hide, close, minimize and move are noticed by a light poll that only
///   reads window state.
/// - Studio never shows a window the app has hidden, and gives the window back where the app had it.
/// </summary>
internal sealed class NativeAppSurface : IDisposable
{
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoZOrder = 0x0004, NoActivate = 0x0010, NoOwnerZOrder = 0x0200, Async = 0x4000;
    private static readonly nint NotTopmost = -2, Topmost = -1;
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Queue<long> corrections = new();
    private nint handle;
    private uint processId;
    private NativeRect originalRect;
    private bool originalMaximized, originalTopmost, docked, minimized, reported, inFront;
    private nint studio;
    private Rect target = Rect.Empty;

    /// <summary>The app hid or closed the window. The argument explains what happened.</summary>
    public event Action<string>? Lost;
    /// <summary>App view could not keep the window in place. The window has already been handed back.</summary>
    public event Action<string>? Failed;
    public event Action<bool>? MinimizedChanged;
    /// <summary>Diagnostic lines for Studio's log: every placement decision with the target and the window's real rectangle.</summary>
    public Action<string>? Log;

    public NativeAppSurface()
    {
        watch.Tick += (_, _) => Check();
    }

    public nint Handle => Alive ? handle : 0;
    public bool IsDocked => docked && Alive;
    public bool Alive => handle != 0 && IsWindow(handle) && GetWindowThreadProcessId(handle, out var pid) != 0 && pid == processId;

    public void Select(nint window)
    {
        if (window == handle && Alive) { Suspend(); return; }
        Dispose();
        if (!IsShown(window)) throw new InvalidOperationException("That window is not open any more. Open it in the app, then choose it again.");
        GetWindowThreadProcessId(window, out processId);
        if (processId == Environment.ProcessId) throw new InvalidOperationException("Choose the app you want to record, not Studio itself.");
        if (!GetWindowRect(window, out originalRect)) throw new InvalidOperationException("That window is not available.");
        originalMaximized = IsZoomed(window);
        if (IsIconic(window))
        {
            // A minimized window's rectangle is the off-screen icon placeholder. Its real place is the restore
            // position, which Windows keeps in work-area coordinates.
            var placement = new AppWindows.WindowPlacement { Length = Marshal.SizeOf<AppWindows.WindowPlacement>() };
            if (!AppWindows.GetWindowPlacement(window, ref placement)) throw new InvalidOperationException("That window is not available.");
            var normal = placement.NormalPosition;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var monitor = MonitorFromRect(ref normal, 2);
            var (dx, dy) = GetMonitorInfo(monitor, ref info) ? (info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top) : (0, 0);
            originalRect = new NativeRect { Left = normal.Left + dx, Top = normal.Top + dy, Right = normal.Right + dx, Bottom = normal.Bottom + dy };
            originalMaximized = (placement.Flags & 0x2) != 0; // WPF_RESTORETOMAXIMIZED
        }
        originalTopmost = (GetWindowLongPtr(window, -20).ToInt64() & 0x8) != 0;
        handle = window;
        reported = false;
        minimized = IsIconic(window);
        watch.Start();
        Log?.Invoke($"select {window} rect={Show(originalRect)} maximized={originalMaximized} topmost={originalTopmost} minimized={minimized}");
    }

    /// <summary>
    /// Moves the window over the App view rectangle (physical screen pixels). In front, the user operates the app
    /// there. Behind (3D view), the app stays at the same spot just underneath Studio: still running and still feeding
    /// the live 3D preview, but never appearing on the desktop outside Studio.
    /// </summary>
    public void Place(Rect physicalBounds, bool front, nint studioWindow)
    {
        if (!Alive) throw new InvalidOperationException("The selected application window is no longer available.");
        if (!IsWindowVisible(handle)) throw new InvalidOperationException("The app has hidden this window. Open it again from the app.");
        if (physicalBounds.Width < 100 || physicalBounds.Height < 100) return;
        if (target != physicalBounds || front != inFront || !docked) Log?.Invoke($"place target={physicalBounds} front={front} first={!docked}");
        target = physicalBounds;
        var changed = !docked || front != inFront;
        inFront = front;
        studio = studioWindow;
        if (!docked)
        {
            docked = true;
            if (IsIconic(handle) || IsZoomed(handle)) ShowWindowAsync(handle, 9);
        }
        if (changed) { if (front) KeepAbove(); else KeepBehind(); }
        Move();
    }

    public bool InFront => docked && inFront;

    /// <summary>Puts the hosted window just above Studio again, without taking focus. Called when Studio is activated.</summary>
    public void KeepAbove()
    {
        if (!docked || !inFront || !Alive) return;
        // HWND_NOTOPMOST: top of the normal windows (so above Studio), and clears a topmost flag while hosted.
        SetWindowPos(handle, NotTopmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate | NoOwnerZOrder | Async);
    }

    /// <summary>Places the hosted window directly underneath Studio, without taking focus.</summary>
    private void KeepBehind()
    {
        if (!docked || !Alive || studio == 0) return;
        // Clear a topmost flag first, then slot in right below Studio.
        SetWindowPos(handle, NotTopmost, 0, 0, 0, 0, NoMove | NoSize | NoActivate | NoOwnerZOrder | Async);
        SetWindowPos(handle, studio, 0, 0, 0, 0, NoMove | NoSize | NoActivate | NoOwnerZOrder | Async);
    }

    /// <summary>Returns the window to where the app had it. Safe to call repeatedly; never shows a hidden window.</summary>
    public void Suspend()
    {
        if (!docked) return;
        docked = false;
        target = Rect.Empty;
        corrections.Clear();
        if (!Alive) return;
        var r = originalRect;
        Log?.Invoke($"release to {Show(r)} maximized={originalMaximized}");
        SetWindowPos(handle, originalTopmost ? Topmost : NotTopmost, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, NoActivate | NoOwnerZOrder | Async);
        if (originalMaximized && IsWindowVisible(handle)) ShowWindowAsync(handle, 3);
    }

    /// <summary>Restores a minimized hosted window. Only ever applied to a window the app is showing.</summary>
    public void Unminimize()
    {
        if (Alive && IsWindowVisible(handle) && IsIconic(handle)) ShowWindowAsync(handle, 9);
    }

    public Rect Bounds => Alive && GetWindowRect(handle, out var rect) ? new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : Rect.Empty;

    public void Dispose()
    {
        watch.Stop();
        try { Suspend(); }
        finally
        {
            handle = 0;
            processId = 0;
            docked = false;
        }
    }

    private void Move()
    {
        SetWindowPos(handle, 0, (int)Math.Round(target.X), (int)Math.Round(target.Y), (int)Math.Round(target.Width), (int)Math.Round(target.Height), NoZOrder | NoActivate | NoOwnerZOrder | Async);
    }

    /// <summary>Reads window state only; nothing here sends the app a message or waits on it.</summary>
    private void Check()
    {
        if (handle == 0 || reported) return;
        try
        {
            if (!Alive) { Report("The app closed this window."); return; }
            if (!IsWindowVisible(handle)) { Report("The app hid this window. Many apps do this when you close them to the tray."); return; }
            var nowMinimized = IsIconic(handle);
            if (nowMinimized != minimized)
            {
                minimized = nowMinimized;
                MinimizedChanged?.Invoke(minimized);
                if (!minimized && docked) Move();
            }
            if (docked && !minimized) KeepInPlace();
        }
        catch (Exception error) { Trace.WriteLine(error); }
    }

    private void KeepInPlace()
    {
        if (target.IsEmpty || !GetWindowRect(handle, out var actual)) return;
        if (IsZoomed(handle)) { ShowWindowAsync(handle, 9); return; }
        var width = actual.Right - actual.Left;
        var height = actual.Bottom - actual.Top;
        if (Math.Abs(actual.Left - target.X) <= 2 && Math.Abs(actual.Top - target.Y) <= 2 &&
            Math.Abs(width - target.Width) <= 2 && Math.Abs(height - target.Height) <= 2) return;
        var now = Stopwatch.GetTimestamp();
        Log?.Invoke($"correct actual={Show(actual)} target={target}");
        corrections.Enqueue(now);
        while (corrections.Count > 0 && Stopwatch.GetElapsedTime(corrections.Peek(), now) > TimeSpan.FromSeconds(3)) corrections.Dequeue();
        if (corrections.Count > 6)
        {
            // Either the app has a minimum size bigger than App view, or it keeps positioning itself (a sliding
            // shelf, for example). Give the window back instead of fighting the app.
            var tooSmall = width > target.Width + 3 || height > target.Height + 3;
            Log?.Invoke($"gave up after {corrections.Count} corrections, tooSmall={tooSmall}");
            Suspend();
            Failed?.Invoke(tooSmall
                ? "This app's minimum size is larger than App view. Make the Studio window bigger, or use 3D view and operate the app on your desktop."
                : "This app keeps moving its own window, so App view cannot hold it. Use 3D view and operate the app on your desktop.");
            return;
        }
        Move();
    }

    private static string Show(NativeRect r) => $"({r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top})";

    private void Report(string message)
    {
        Log?.Invoke("lost: " + message);
        reported = true;
        watch.Stop();
        try { Suspend(); } catch (Exception error) { Trace.WriteLine(error); }
        Lost?.Invoke(message);
    }

    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
