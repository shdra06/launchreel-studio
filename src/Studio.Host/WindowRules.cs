namespace Studio.Host;

/// <summary>Main windows appear in the taskbar. Floating windows (shelves, palettes, widgets) are owned or tool windows.</summary>
public enum WindowRole { Main, Floating }

public sealed record AppWindow(nint Handle, string Title, int Pid, string ProcessName, string? Path, WindowRole Role, int Width, int Height, bool Minimized, string? AppId = null);

/// <summary>Pure selection rules, kept free of Win32 calls so they can be unit tested.</summary>
public static class WindowRules
{
    public const int MinimumWidth = 160, MinimumHeight = 120;

    /// <summary>Windows' own taskbar rule: an app window, or an unowned window that is not a tool window.</summary>
    public static WindowRole Classify(bool hasOwner, bool toolWindow, bool appWindow) =>
        appWindow || (!hasOwner && !toolWindow) ? WindowRole.Main : WindowRole.Floating;

    /// <summary>
    /// Only windows the application itself is showing are candidates. Hidden, cloaked, untitled, tiny and off-screen
    /// windows belong to the app's internals (for example WPF's "Hidden Window" parking owner) and are never revealed.
    /// </summary>
    public static bool Eligible(bool visible, bool cloaked, string title, int width, int height, bool onMonitor, bool minimized) =>
        visible && !cloaked && !string.IsNullOrWhiteSpace(title) && width >= MinimumWidth && height >= MinimumHeight && (onMonitor || minimized);

    /// <summary>
    /// Picks a window without asking only when the choice is unambiguous: the window the user was already using,
    /// otherwise the single main window. Floating windows are never chosen automatically.
    /// </summary>
    public static AppWindow? AutoChoice(IReadOnlyList<AppWindow> candidates, string? preferredTitle = null)
    {
        if (preferredTitle is not null)
        {
            var same = candidates.Where(c => string.Equals(c.Title, preferredTitle, StringComparison.Ordinal)).ToList();
            if (same.Count == 1) return same[0];
        }
        var main = candidates.Where(c => c.Role == WindowRole.Main).ToList();
        return main.Count == 1 ? main[0] : null;
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// The same app, even when it runs from another folder (a release copy and a development build, for example):
    /// the same file name and the same product name. Starting a second copy of a single-instance app would make the
    /// running one react (FlyShelf toggles its shelf), so any copy that is already running counts.
    /// </summary>
    public static bool SameApp(string? pathA, string? productA, string? pathB, string? productB)
    {
        if (SamePath(pathA, pathB)) return true;
        if (string.IsNullOrEmpty(pathA) || string.IsNullOrEmpty(pathB) || string.IsNullOrWhiteSpace(productA)) return false;
        return string.Equals(System.IO.Path.GetFileName(pathA), System.IO.Path.GetFileName(pathB), StringComparison.OrdinalIgnoreCase)
            && string.Equals(productA.Trim(), productB?.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Largest even-sized rectangle with the source aspect ratio that fits inside the destination, centered.</summary>
    public static (int X, int Y, int Width, int Height) Fit(int sourceWidth, int sourceHeight, int width, int height)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0) return (0, 0, 0, 0);
        var scale = Math.Min(width / (double)sourceWidth, height / (double)sourceHeight);
        var w = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, width);
        var h = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, height);
        return ((width - w) / 2, (height - h) / 2, w, h);
    }

    /// <summary>Number of frames a constant-rate recording must contain after <paramref name="elapsedSeconds"/> of wall-clock time.</summary>
    public static long FramesDue(double elapsedSeconds, int fps) =>
        elapsedSeconds < 0 || !double.IsFinite(elapsedSeconds) ? 0 : (long)Math.Floor(elapsedSeconds * fps) + 1;
}
