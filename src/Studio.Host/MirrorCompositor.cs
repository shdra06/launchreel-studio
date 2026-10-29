using System.Runtime.InteropServices;
using System.Text;

namespace Studio.Host;

/// <summary>
/// A live mirror of a running app: its main window plus the pop-ups it opens over it (menus, dropdowns, tooltips,
/// previews), composited at their real offsets in the app's own stacking order. Pop-ups are separate windows that a
/// single window capture never includes. Everything here only reads the app's windows; it never moves or shows them.
/// </summary>
public sealed class MirrorCompositor(nint window, uint pid) : IDisposable
{
    private const int Reach = 600; // pop-ups up to this far outside the window still count as the app's
    private readonly Dictionary<nint, NativeCapture> popups = [];
    private readonly object gate = new();
    private List<nint> order = [];
    private string signature = "";
    private CapturedFrame? composed;
    private long sequence;

    public nint Window => window;
    public uint Pid => pid;
    /// <summary>Pop-ups first (topmost first), then the main window: the order input looks for a target in.</summary>
    public IReadOnlyList<nint> Windows { get { lock (gate) return [.. order, window]; } }

    /// <summary>Starts or stops pop-up captures to match what the app is showing right now. Call a few times a second.</summary>
    public void Refresh()
    {
        if (!GetWindowRect(window, out var main)) return;
        var found = new List<nint>();
        EnumWindows((h, _) =>
        {
            if (h == window || !IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var p);
            if (p != pid || !GetWindowRect(h, out var r) || r.Right - r.Left < 8 || r.Bottom - r.Top < 8) return true;
            if (r.Right < main.Left - Reach || r.Left > main.Right + Reach || r.Bottom < main.Top - Reach || r.Top > main.Bottom + Reach) return true;
            if (DwmGetWindowAttribute(h, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;
            if (IsZOrderBelow(h)) return true;
            found.Add(h);
            return true;
        }, 0);
        lock (gate)
        {
            foreach (var gone in popups.Keys.Where(h => !found.Contains(h)).ToList()) { popups[gone].Dispose(); popups.Remove(gone); }
            foreach (var h in found.Where(h => !popups.ContainsKey(h)))
            {
                var capture = new NativeCapture { MaxRate = 30 };
                try { capture.Start(h); popups[h] = capture; } catch { capture.Dispose(); }
            }
            order = found.Where(popups.ContainsKey).ToList();
        }
    }

    /// <summary>Only windows stacked above the main window are its pop-ups; the app's other windows behind it are not shown.</summary>
    private bool IsZOrderBelow(nint candidate)
    {
        for (var w = GetWindow(window, 3); w != 0; w = GetWindow(w, 3)) if (w == candidate) return false; // GW_HWNDPREV walks upwards
        return true;
    }

    /// <summary>The main picture with current pop-ups drawn over it. Rebuilt only when something changed.</summary>
    public CapturedFrame? Compose(CapturedFrame? main)
    {
        if (main is null) return null;
        List<(CapturedFrame Frame, (int X, int Y) Origin)> layers;
        lock (gate) layers = order.AsEnumerable().Reverse().Select(h => (popups[h].Latest, WindowInput.Origin(h))).Where(l => l.Latest is not null).Select(l => (l.Latest!, l.Item2)).ToList();
        var key = $"{main.Sequence}|{string.Join(',', layers.Select(l => $"{l.Frame.Sequence}@{l.Origin}"))}";
        if (key == signature && composed is not null) return composed;
        if (layers.Count == 0) { signature = key; return composed = main; }
        var origin = WindowInput.Origin(window);
        var pixels = (byte[])main.Pixels.Clone();
        foreach (var (frame, at) in layers)
        {
            int dx = at.X - origin.X, dy = at.Y - origin.Y;
            Blend(frame, pixels, main.Width, main.Height, dx, dy);
        }
        signature = key;
        return composed = new CapturedFrame(pixels, main.Width, main.Height, main.Timestamp, (1L << 40) + Interlocked.Increment(ref sequence));
    }

    /// <summary>Premultiplied "over": pop-up shadows and rounded corners blend instead of turning into black boxes.</summary>
    internal static void Blend(CapturedFrame source, byte[] target, int width, int height, int dx, int dy)
    {
        var src = MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan());
        var dst = MemoryMarshal.Cast<byte, uint>(target.AsSpan());
        var x0 = Math.Max(0, -dx); var x1 = Math.Min(source.Width, width - dx);
        if (x1 <= x0) return;
        for (var y = Math.Max(0, -dy); y < source.Height && y + dy < height; y++)
        {
            var srow = y * source.Width; var drow = (y + dy) * width + dx;
            for (var x = x0; x < x1; x++)
            {
                var s = src[srow + x]; var a = s >> 24;
                if (a == 255) { dst[drow + x] = s; continue; }
                if (a == 0) continue;
                var d = dst[drow + x]; var k = 255 - a;
                uint Channel(int shift) => Math.Min(255u, ((s >> shift) & 0xFF) + (((d >> shift) & 0xFF) * k + 127) / 255);
                dst[drow + x] = Channel(0) | Channel(8) << 8 | Channel(16) << 16 | Math.Min(255u, a + (((d >> 24) & 0xFF) * k + 127) / 255) << 24;
            }
        }
    }

    public void Dispose()
    {
        lock (gate) { foreach (var c in popups.Values) c.Dispose(); popups.Clear(); order = []; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(nint h, nint l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out NativeRect r);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint h, uint command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint h, int attribute, out int value, int size);
}
