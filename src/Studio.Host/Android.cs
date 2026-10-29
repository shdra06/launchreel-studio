using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Studio.Host;

public sealed record AndroidDevice(string Serial, string State, string Model, bool Emulator);

/// <summary>
/// Android apps, on a phone over USB or Wi-Fi debugging, or in the Android Emulator. adb (Android's own debugging
/// tool) lists devices and starts apps exactly like tapping their icon; scrcpy shows the device's real screen in a
/// window on this PC, which Studio then captures like any other app window. Studio never installs, removes or
/// changes apps or settings on the device.
/// </summary>
internal static partial class AndroidTools
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    public static string? ScrcpyOverride { get; set; }

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static IEnumerable<string> SdkRoots() => new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"), Path.Combine(Local, "Android", "Sdk") }
        .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!);

    public static string? Adb => MediaService.FindTool("adb") ?? SdkRoots().Select(r => Path.Combine(r, "platform-tools", "adb.exe")).FirstOrDefault(File.Exists)
        ?? (Scrcpy is { } s && Path.Combine(Path.GetDirectoryName(s)!, "adb.exe") is var bundled && File.Exists(bundled) ? bundled : null);

    public static string? Emulator => SdkRoots().Select(r => Path.Combine(r, "emulator", "emulator.exe")).FirstOrDefault(File.Exists);

    /// <summary>scrcpy from PATH, a winget or Scoop install, or the copy chosen in Studio.</summary>
    public static string? Scrcpy
    {
        get
        {
            if (ScrcpyOverride is { } chosen && File.Exists(chosen)) return chosen;
            if (MediaService.FindTool("scrcpy") is { } found) return found;
            var links = Path.Combine(Local, "Microsoft", "WinGet", "Links", "scrcpy.exe");
            if (File.Exists(links)) return links;
            foreach (var folder in new[] { Path.Combine(Local, "Microsoft", "WinGet", "Packages"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "scrcpy") })
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    var match = Directory.EnumerateDirectories(folder).Where(d => folder.EndsWith("scrcpy", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(d).Contains("scrcpy", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(d => Directory.EnumerateFiles(d, "scrcpy.exe", SearchOption.AllDirectories)).FirstOrDefault();
                    if (match is not null) return match;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return null;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,128}$")] private static partial Regex SerialPattern();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")] private static partial Regex PackagePattern();
    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,128}$")] private static partial Regex AvdPattern();
    public static string Serial(string value) => SerialPattern().IsMatch(value) ? value : throw new InvalidDataException("Invalid Android device.");
    public static string Package(string value) => value.Length <= 255 && PackagePattern().IsMatch(value) ? value : throw new InvalidDataException("Invalid Android app.");

    private static async Task<string> Run(string tool, params string[] args)
    {
        using var limit = new CancellationTokenSource(Timeout);
        try { return await MediaService.Run(tool, args, limit.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("The Android device did not answer. Check that it is unlocked and connected."); }
    }

    private static string RequireAdb() => Adb ?? throw new FileNotFoundException("Android support needs adb. Install Android Studio, or scrcpy, which includes adb: winget install --id Genymobile.scrcpy");

    public static async Task<List<AndroidDevice>> Devices()
    {
        var output = await Run(RequireAdb(), "devices", "-l");
        var devices = new List<AndroidDevice>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !SerialPattern().IsMatch(parts[0])) continue;
            var model = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?[6..].Replace('_', ' ') ?? parts[0];
            devices.Add(new AndroidDevice(parts[0], parts[1], model, parts[0].StartsWith("emulator-", StringComparison.Ordinal)));
        }
        return devices;
    }

    /// <summary>Apps with a launcher icon, as package names.</summary>
    public static async Task<List<string>> Apps(string serial)
    {
        var output = await Run(RequireAdb(), "-s", Serial(serial), "shell", "cmd", "package", "query-activities", "--brief", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER");
        var apps = output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains('/')).Select(l => l[..l.IndexOf('/')]).Where(p => PackagePattern().IsMatch(p));
        if (!apps.Any())
        {
            // Older Android versions have no query-activities: fall back to the installed packages.
            output = await Run(RequireAdb(), "-s", serial, "shell", "pm", "list", "packages");
            apps = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:", StringComparison.Ordinal)).Select(l => l[8..]).Where(p => PackagePattern().IsMatch(p));
        }
        return apps.Distinct().OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Starts the app as its launcher icon does. A running app comes to the front; no second copy starts.</summary>
    public static Task Start(string serial, string package) =>
        Run(RequireAdb(), "-s", Serial(serial), "shell", "monkey", "-p", Package(package), "-c", "android.intent.category.LAUNCHER", "1");

    public static async Task<List<string>> VirtualDevices()
    {
        if (Emulator is not { } emulator) return [];
        try { return (await Run(emulator, "-list-avds")).Split('\n').Select(l => l.Trim()).Where(a => AvdPattern().IsMatch(a)).ToList(); }
        catch (IOException) { return []; }
    }

    public static void StartEmulator(string avd)
    {
        var emulator = Emulator ?? throw new FileNotFoundException("The Android Emulator was not found. Install it with Android Studio.");
        if (!AvdPattern().IsMatch(avd)) throw new InvalidDataException("Invalid virtual device.");
        // The emulator is the user's own device: it keeps running when Studio closes.
        Process.Start(new ProcessStartInfo(emulator) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "-avd", avd } })?.Dispose();
    }

    /// <summary>
    /// Opens a live scrcpy mirror of the device screen. It has no window border, so recordings show only the
    /// device's own pixels, and no audio forwarding, so the device keeps playing its sound itself.
    /// </summary>
    public static Process Mirror(string serial, string title)
    {
        var scrcpy = Scrcpy ?? throw new FileNotFoundException("A live Android mirror needs scrcpy: winget install --id Genymobile.scrcpy (or choose scrcpy.exe in Settings).");
        var info = new ProcessStartInfo(scrcpy) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(scrcpy) };
        foreach (var arg in new[] { "-s", Serial(serial), "--window-title", title, "--window-borderless", "--no-audio", "--video-bit-rate", "16M" }) info.ArgumentList.Add(arg);
        if (Adb is { } adb) info.Environment["ADB"] = adb;
        var process = Process.Start(info) ?? throw new IOException("scrcpy did not start.");
        var log = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Logs[process.Id] = log;
        return process;
    }

    public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, StringBuilder> Logs = new();
    public static string LastError(int pid)
    {
        if (!Logs.TryGetValue(pid, out var log)) return "";
        string text; lock (log) text = log.ToString();
        var error = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));
        return error ?? "";
    }

    /// <summary>The display size apps see, after any resolution override.</summary>
    public static async Task<(int Width, int Height)> ScreenSize(string serial)
    {
        var output = await Run(RequireAdb(), "-s", Serial(serial), "shell", "wm", "size");
        (int, int)? size = null;
        foreach (var line in output.Split('\n'))
        {
            var m = Regex.Match(line, @"(\d+)x(\d+)");
            if (m.Success) size = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)); // "Override size" comes last and wins
        }
        return size ?? throw new IOException("Could not read the device's screen size.");
    }
}

/// <summary>
/// Live 3D input for an Android mirror: taps, swipes, scrolling and typing are sent to the device through adb's
/// own input command, so the PC's cursor, focus and windows never change. Picture pixels are mapped through the
/// mirror window, which shows the device screen scaled to fit.
/// </summary>
internal sealed class AndroidInput(string serial, nint window, (int Width, int Height) screen) : IDisposable
{
    private readonly object gate = new();
    private Process? shell;
    private (int X, int Y) downAt;
    private long downTime;
    private bool down;
    public nint Window => window;
    public string Serial => serial;

    private void Send(string command)
    {
        lock (gate)
        {
            if (shell is null || shell.HasExited)
            {
                shell?.Dispose();
                shell = MediaService.Start(AndroidTools.Adb ?? throw new FileNotFoundException("adb was not found."), ["-s", serial, "shell"], redirectInput: true);
                shell.OutputDataReceived += (_, _) => { };
                shell.ErrorDataReceived += (_, _) => { };
                shell.BeginOutputReadLine();
                shell.BeginErrorReadLine();
            }
            shell.StandardInput.WriteLine(command);
            shell.StandardInput.Flush();
        }
    }

    /// <summary>Maps a pixel of the captured mirror window to device coordinates; null outside the device picture.</summary>
    private (int X, int Y)? Device(double x, double y)
    {
        if (!GetClientRect(window, out var client)) return null;
        var origin = WindowInput.Origin(window);
        var topLeft = new NativePoint();
        ClientToScreen(window, ref topLeft);
        int cw = client.Right, ch = client.Bottom;
        // The device may be rotated: the mirror's shape tells which way round its screen is.
        var (w, h) = (cw > ch) == (screen.Width > screen.Height) ? screen : (screen.Height, screen.Width);
        var fit = WindowRules.Fit(w, h, cw, ch);
        if (fit.Width <= 0) return null;
        var px = x - (topLeft.X - origin.X) - fit.X;
        var py = y - (topLeft.Y - origin.Y) - fit.Y;
        if (px < 0 || py < 0 || px >= fit.Width || py >= fit.Height) return null;
        return ((int)Math.Round(px * w / fit.Width), (int)Math.Round(py * h / fit.Height));
    }

    public void Pointer(JsonObject data)
    {
        var action = data["action"]?.GetValue<string>();
        if ((data["button"]?.GetValue<int>() ?? 0) != 0) return;
        var point = Device(data["x"]!.GetValue<double>(), data["y"]!.GetValue<double>());
        switch (action)
        {
            case "pointerDown" when point is { } p:
                down = true; downAt = p; downTime = Environment.TickCount64;
                break;
            case "pointerUp" when down:
            {
                down = false;
                var end = point ?? downAt;
                var held = (int)Math.Clamp(Environment.TickCount64 - downTime, 40, 3000);
                if (Math.Abs(end.X - downAt.X) < 12 && Math.Abs(end.Y - downAt.Y) < 12)
                    Send(held > 500 ? $"input swipe {downAt.X} {downAt.Y} {downAt.X} {downAt.Y} {held}" : $"input tap {downAt.X} {downAt.Y}");
                else Send($"input swipe {downAt.X} {downAt.Y} {end.X} {end.Y} {held}");
                break;
            }
            case "scroll" when point is { } p:
            {
                var distance = (int)(Math.Sign(data["delta"]?.GetValue<double>() ?? 0) * screen.Height * .12);
                if (distance != 0) Send($"input swipe {p.X} {p.Y} {p.X} {Math.Clamp(p.Y - distance, 0, Math.Max(screen.Width, screen.Height))} 120");
                break;
            }
        }
    }

    public void Key(JsonObject data)
    {
        if (data["type"]?.GetValue<string>() == "up") return;
        var vk = data["vk"]?.GetValue<int>() ?? 0;
        int? keycode = vk switch { 0x0D => 66, 0x08 => 67, 0x2E => 112, 0x1B => 4, 0x09 => 61, 0x25 => 21, 0x26 => 19, 0x27 => 22, 0x28 => 20, 0x24 => 3, _ => null };
        if (keycode is { } code) { Send($"input keyevent {code}"); return; }
        var text = data["key"]?.GetValue<string>();
        if (text is { Length: 1 } && !char.IsControl(text[0]))
            Send(text == " " ? "input keyevent 62" : $"input text '{text.Replace("'", "'\\''")}'");
    }

    public void Dispose()
    {
        lock (gate)
        {
            try { if (shell is { HasExited: false }) { shell.StandardInput.WriteLine("exit"); if (!shell.WaitForExit(500)) shell.Kill(); } } catch { }
            shell?.Dispose();
            shell = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
