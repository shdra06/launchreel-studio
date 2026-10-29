using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Studio.Host;

/// <summary>Read-only discovery of top-level windows. Nothing here changes another application's windows.</summary>
internal static class AppWindows
{
    public static List<AppWindow> List()
    {
        var result = new List<AppWindow>();
        var processes = new Dictionary<uint, (string Name, string? Path, string? AppId)>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId) return true;
            var window = Describe(hwnd, pid, processes);
            if (window is not null) result.Add(window);
            return true;
        }, 0);
        return result;
    }

    public static List<AppWindow> ForPath(string path) => List().Where(w => SameApp(w.Path, path)).ToList();

    /// <summary>
    /// The windows of a launch target: an .exe path, <c>package:AppId</c> for a Store app, or any target narrowed to
    /// one process (an Android mirror Studio started).
    /// </summary>
    public static List<AppWindow> ForTarget(string target, int pid = 0)
    {
        if (pid != 0) return List().Where(w => w.Pid == pid).ToList();
        if (target.StartsWith(AppCatalog.PackagePrefix, StringComparison.Ordinal))
        {
            var id = target[AppCatalog.PackagePrefix.Length..];
            return List().Where(w => string.Equals(w.AppId, id, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return target.StartsWith(AppCatalog.AndroidPrefix, StringComparison.Ordinal) ? [] : ForPath(target);
    }

    public static bool IsTargetRunning(string target, int pid = 0)
    {
        if (pid != 0) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
        if (target.StartsWith(AppCatalog.PackagePrefix, StringComparison.Ordinal)) return AppCatalog.IsPackageRunning(target[AppCatalog.PackagePrefix.Length..]);
        return !target.StartsWith(AppCatalog.AndroidPrefix, StringComparison.Ordinal) && IsRunning(target);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Products = new(StringComparer.OrdinalIgnoreCase);
    private static string? Product(string path) => Products.GetOrAdd(path, p =>
    {
        try { return FileVersionInfo.GetVersionInfo(p).ProductName; }
        catch (Exception error) when (error is FileNotFoundException or ArgumentException) { return null; }
    });
    /// <summary>Any copy of the same app counts, wherever it runs from. See <see cref="WindowRules.SameApp"/>.</summary>
    public static bool SameApp(string? running, string path) => running is not null && WindowRules.SameApp(running, Product(running), path, Product(path));

    public static AppWindow? Find(nint handle)
    {
        if (!IsWindow(handle)) return null;
        GetWindowThreadProcessId(handle, out var pid);
        return pid == Environment.ProcessId ? null : Describe(handle, pid, []);
    }

    public static bool IsRunning(string path)
    {
        foreach (var process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
                if (SameApp(ProcessPath(process.Id), path)) return true;
        }
        return false;
    }

    public static bool IsShown(nint handle) => IsWindow(handle) && IsWindowVisible(handle) && !Cloaked(handle);

    /// <summary>Full image path via limited query rights, which also works for processes whose modules cannot be read.</summary>
    public static string? ProcessPath(int pid)
    {
        var process = OpenProcess(0x1000, false, (uint)pid);
        if (process == 0) return null;
        try
        {
            var buffer = new StringBuilder(32768);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(process); }
    }

    private static AppWindow? Describe(nint hwnd, uint pid, Dictionary<uint, (string Name, string? Path, string? AppId)> processes)
    {
        var length = GetWindowTextLength(hwnd);
        var builder = new StringBuilder(Math.Max(1, length + 1));
        if (length > 0) GetWindowText(hwnd, builder, builder.Capacity);
        var title = builder.ToString();
        var minimized = IsIconic(hwnd);
        int width, height;
        bool onMonitor;
        if (minimized)
        {
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(hwnd, ref placement)) return null;
            width = placement.NormalPosition.Right - placement.NormalPosition.Left;
            height = placement.NormalPosition.Bottom - placement.NormalPosition.Top;
            onMonitor = false;
        }
        else
        {
            if (!GetWindowRect(hwnd, out var rect)) return null;
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            onMonitor = MonitorFromRect(ref rect, 0) != 0;
        }
        if (!WindowRules.Eligible(IsWindowVisible(hwnd), Cloaked(hwnd), title, width, height, onMonitor, minimized)) return null;
        if (!processes.TryGetValue(pid, out var process))
        {
            string name;
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            process = (name, ProcessPath((int)pid), AppCatalog.ProcessAppId(pid));
            processes[pid] = process;
        }
        // UWP apps draw inside a frame window that belongs to ApplicationFrameHost; the app owns the CoreWindow inside it.
        var appId = process.AppId;
        if (appId is null && string.Equals(process.Name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            var core = FindWindowEx(hwnd, 0, "Windows.UI.Core.CoreWindow", null);
            if (core != 0) { GetWindowThreadProcessId(core, out var appPid); appId = AppCatalog.ProcessAppId(appPid); }
        }
        var exStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
        var role = WindowRules.Classify(GetWindow(hwnd, 4) != 0, (exStyle & 0x80) != 0, (exStyle & 0x40000) != 0);
        return new AppWindow(hwnd, title, (int)pid, process.Name, process.Path, role, width, height, minimized, appId);
    }

    private static bool Cloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct WindowPlacement
    {
        public int Length, Flags, ShowCommand;
        public NativePoint MinPosition, MaxPosition;
        public NativeRect NormalPosition;
    }
    private delegate bool EnumProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder title, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] internal static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);
}
