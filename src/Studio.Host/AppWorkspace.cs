using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Studio.Host;

/// <summary>
/// Launching, choosing, hosting and releasing the user's real application. Studio never starts a second copy of an app
/// that is already running and never shows a window the app has hidden: when the app has no window open (for example a
/// tray app at startup), Studio waits until the app itself opens one.
/// </summary>
public partial class MainWindow
{
    private const string BridgeVariable = "STUDIO_BRIDGE_TOKEN";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(10);
    private NativeAppSurface? appSurface;
    private DispatcherTimer? appWatch;
    private Rect? appViewport;
    private bool appView;
    private bool placingApp;
    private nint sourceHandle;
    private string sourceTitle = "";
    private string? sourcePath;
    private WindowRole sourceRole;
    private Wait? waiting;
    private bool connecting;
    private object? connection;
    private MirrorCompositor? mirror;
    private readonly WindowInput windowInput = new();
    private readonly UiaInput uiaInput = new();
    private readonly DispatcherTimer mirrorWatch = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool screenCapture;

    private AndroidInput? androidInput;
    private Process? androidMirror;

    private sealed class Wait
    {
        /// <summary>What to wait for: an .exe path, <c>package:AppId</c> (Store app) or <c>android:serial/package</c>.</summary>
        public required string Path { get; init; }
        /// <summary>Only this process's windows (an Android mirror), or 0.</summary>
        public int Pid { get; init; }
        public string? Serial { get; init; }
        public required string Name { get; init; }
        public required bool AlreadyRunning { get; init; }
        public bool Dock { get; init; } = true;
        public string? PreferredTitle { get; init; }
        public Process? Started { get; init; }
        public DateTime Since { get; } = DateTime.UtcNow;
        public string LastReport { get; set; } = "";
        public int Failures { get; set; }
    }

    private void InitializeAppWorkspace()
    {
        appSurface = new NativeAppSurface { Log = message => CrashLog.Info(root, "app view: " + message) };
        // The app is kept above Studio by z-order only (see NativeAppSurface), so lift it again whenever Studio comes forward.
        Activated += (_, _) => appSurface.KeepAbove();
        appSurface.Lost += message => OnSourceLost(message);
        appSurface.Failed += message => { appView = false; Event("app.viewError", new { message }); };
        appSurface.MinimizedChanged += minimized => Event("app.minimized", new { minimized });
        LocationChanged += (_, _) => Dispatcher.BeginInvoke(PlaceAppSurface, DispatcherPriority.Loaded);
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(PlaceAppSurface, DispatcherPriority.Loaded);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) ReleaseQuietly();
            else Dispatcher.BeginInvoke(PlaceAppSurface, DispatcherPriority.Loaded);
        };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(StudioMessages);
        mirrorWatch.Tick += (_, _) => mirror?.Refresh();
        appWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        appWatch.Tick += (_, _) => WatchApplication();
        appWatch.Start();
    }

    private nint StudioMessages(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Hand the app back to its own desktop position before Studio minimizes, instead of leaving it floating over
        // an empty spot.
        if (message == 0x0112 && (wParam.ToInt64() & 0xFFF0) == 0xF020) ReleaseQuietly();
        return 0;
    }

    private void ReleaseQuietly()
    {
        try { appSurface?.Suspend(); } catch (Exception error) { Trace.WriteLine(error); }
    }

    private bool watching;
    private async void WatchApplication()
    {
        // Floating windows are not held by the App view surface, so their state is watched here. Reads only.
        if (sourceHandle != 0 && sourceRole == WindowRole.Floating && !AppWindows.IsShown(sourceHandle))
            OnSourceLost(AppWindows.IsWindow(sourceHandle) ? "The app hid this window. Many apps do this when you close them to the tray." : "The app closed this window.");
        if (waiting is null || watching) return;
        watching = true;
        try { await PollWaiting(); }
        catch (Exception error) { Trace.WriteLine(error); }
        finally { watching = false; }
    }

    private object Describe(AppWindow w) => new
    {
        handle = w.Handle.ToString(), title = w.Title, pid = w.Pid, app = w.ProcessName, path = w.Path,
        role = w.Role == WindowRole.Main ? "main" : "floating", width = w.Width, height = w.Height, minimized = w.Minimized
    };

    /// <summary>Window scanning queries every process, so it never runs on Studio's UI thread.</summary>
    private static Task<List<AppWindow>> Scan(Wait wait) => Task.Run(() => AppWindows.ForTarget(wait.Path, wait.Pid));

    private async Task<object[]> ListWindows() => (await Task.Run(AppWindows.List))
        .OrderBy(w => w.Role).ThenBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
        .Select(Describe).ToArray();

    private async Task<object> LaunchApplication(string path, bool dock = true)
    {
        if (recorder is not null) throw new InvalidOperationException("Finish the current recording before launching another app.");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Choose an app (.exe) or a shortcut that starts one. Store apps are in the Installed apps list.");
        if (WindowRules.SamePath(fullPath, Environment.ProcessPath)) throw new InvalidOperationException("Choose the app you want to record, not Studio itself.");
        StopWaiting();
        RememberApp(fullPath, AppName(fullPath));
        var running = await Task.Run(() => AppWindows.IsRunning(fullPath));
        // A running app is never started again: single-instance apps react to a second launch in their own way
        // (a tray app may toggle its window, for example), which would change what the user is about to record.
        var started = running ? null : StartApp(fullPath);
        return await BeginWait(new Wait { Path = fullPath, Name = AppName(fullPath), AlreadyRunning = running, Started = started, Dock = dock });
    }

    /// <summary>A Store or packaged app (UWP, WinUI, MSIX), started the way its Start menu tile starts it.</summary>
    private async Task<object> LaunchPackage(string appId, string name, bool dock = true)
    {
        if (recorder is not null) throw new InvalidOperationException("Finish the current recording before launching another app.");
        if (!AppCatalog.IsPackageId(appId)) throw new InvalidDataException("Invalid app.");
        StopWaiting();
        var target = AppCatalog.PackagePrefix + appId;
        RememberApp(target, name);
        var running = await Task.Run(() => AppCatalog.IsPackageRunning(appId));
        Process? started = null;
        if (!running)
        {
            var pid = await Task.Run(() => AppCatalog.Activate(appId));
            try { started = Process.GetProcessById((int)pid); } catch (ArgumentException) { }
        }
        return await BeginWait(new Wait { Path = target, Name = name, AlreadyRunning = running, Started = started, Dock = dock });
    }

    /// <summary>
    /// An Android app on a connected phone or emulator: started like a tap on its icon, then shown through a live
    /// scrcpy mirror that Studio captures and operates (adb input) like any app window.
    /// </summary>
    private async Task<object> LaunchAndroid(string serial, string package, string? model, bool dock = true)
    {
        if (recorder is not null) throw new InvalidOperationException("Finish the current recording before launching another app.");
        AndroidTools.Serial(serial); AndroidTools.Package(package);
        StopWaiting();
        var target = AppCatalog.AndroidPrefix + serial + "/" + package;
        var device = string.IsNullOrWhiteSpace(model) ? serial : model.Trim();
        var name = $"{package} · {device}";
        RememberApp(target, name);
        await AndroidTools.Start(serial, package);
        if (AndroidTools.Scrcpy is null)
            throw new FileNotFoundException(serial.StartsWith("emulator-", StringComparison.Ordinal)
                ? $"{package} is open in the emulator. Choose the emulator's window under Open windows, or install scrcpy for a borderless live mirror: winget install --id Genymobile.scrcpy"
                : $"{package} is open on {device}. To show it here, install scrcpy (winget install --id Genymobile.scrcpy) or choose scrcpy.exe in Settings, then launch it again.");
        // The mirror shows the whole device screen, so an open mirror of this device already shows the app.
        if (sourceHandle != 0 && androidInput?.Serial == serial && connection is not null) return connection;
        if (sourceHandle != 0 && androidMirror is not null) ReleaseSource();
        StopAndroidMirror();
        var mirror = AndroidTools.Mirror(serial, $"{package} · Android");
        androidMirror = mirror;
        return await BeginWait(new Wait { Path = target, Name = name, AlreadyRunning = false, Started = mirror, Dock = dock, Pid = mirror.Id, Serial = serial });
    }

    private void StopAndroidMirror()
    {
        var mirror = androidMirror;
        androidMirror = null;
        if (mirror is null) return;
        // The mirror is Studio's own helper window; the app keeps running on the device.
        try { if (!mirror.HasExited) mirror.Kill(); } catch (Exception error) { Trace.WriteLine(error); }
        AndroidTools.Logs.TryRemove(mirror.Id, out _);
        mirror.Dispose();
    }

    private async Task<object> BeginWait(Wait wait)
    {
        var started = wait.Started;
        waiting = wait;
        if (started is not null)
            for (var i = 0; i < 20 && waiting is not null; i++)
            {
                var connected = await PollWaiting();
                if (connected is not null) return connected;
                await Task.Delay(150);
            }
        // The background watch may be connecting, or have connected, while this method was waiting. Let a connection
        // in progress finish instead of reporting "idle" in the moment between the wait ending and it being recorded.
        for (var i = 0; connecting && i < 200; i++) await Task.Delay(50);
        if (sourceHandle != 0 && connection is not null && waiting is null) return connection;
        return await PollWaiting() ?? await WaitingState(force: true) ?? (sourceHandle != 0 ? connection : null) ?? new { state = "idle" };
    }

    private Process StartApp(string path)
    {
        // The app is started through the Windows shell, exactly as a double-click in Explorer would start it: its
        // own working folder, a fresh environment, no handles inherited from Studio. Only Studio's automated checks
        // start their test window directly, to hand it the development bridge token.
        var bridged = selfTest is not null || appViewTest is not null || mirrorTest is not null;
        var start = new ProcessStartInfo(path) { UseShellExecute = !bridged, WorkingDirectory = Path.GetDirectoryName(path) };
        var token = bridged ? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) : null;
        if (token is not null) start.Environment[BridgeVariable] = token;
        try
        {
            var process = Process.Start(start) ?? throw new IOException("The app did not start.");
            if (token is not null) tokens[process.Id] = token;
            return process;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The app was not started.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 740)
        {
            throw new InvalidOperationException("This app asks for administrator rights. Studio can only host apps running at normal permission. Start it normally, then choose its window.");
        }
    }

    /// <summary>Checks for the app's windows. Connects when the choice is unambiguous; otherwise reports candidates.</summary>
    private async Task<object?> PollWaiting()
    {
        var wait = waiting;
        if (wait is null || connecting) return null;
        var candidates = await Scan(wait);
        if (waiting != wait) return null;
        var choice = recorder is null ? WindowRules.AutoChoice(candidates, wait.PreferredTitle) : null;
        if (choice is not null)
        {
            try { return await ConnectApplication(choice.Handle, wait.Dock); }
            catch (Exception error)
            {
                // A window that has only just appeared can still be settling (briefly cloaked, resizing), so
                // try again on the next poll before telling the user anything went wrong.
                if (++wait.Failures < 5) return null;
                StopWaiting(); Event("app.waitEnded", new { message = error.Message }); return null;
            }
        }
        if (wait.Started is { HasExited: true } && candidates.Count == 0 && (wait.Pid != 0 || !await Task.Run(() => AppWindows.IsTargetRunning(wait.Path))))
        {
            var detail = wait.Pid != 0 ? AndroidTools.LastError(wait.Pid) : "";
            StopWaiting();
            Event("app.waitEnded", new { message = wait.Pid != 0
                ? $"The Android mirror closed before it showed the screen. {(detail.Length > 0 ? detail + " " : "")}Check that the device is unlocked and USB debugging is allowed."
                : $"{wait.Name} closed before it opened a window." });
            return null;
        }
        if (DateTime.UtcNow - wait.Since > WaitLimit)
        {
            StopWaiting();
            Event("app.waitEnded", new { message = $"Stopped waiting for {wait.Name}. Launch it again when you are ready." });
            return null;
        }
        await WaitingState(force: false, candidates);
        return null;
    }

    private async Task<object?> WaitingState(bool force, List<AppWindow>? found = null)
    {
        var wait = waiting;
        if (wait is null) return null;
        var candidates = (found ?? await Scan(wait)).Select(Describe).ToArray();
        if (waiting != wait) return null;
        var state = new { state = "waiting", name = wait.Name, path = wait.Path, alreadyRunning = wait.AlreadyRunning, reconnect = wait.PreferredTitle, candidates };
        var report = JsonSerializer.Serialize(state);
        if (force || report != wait.LastReport) { wait.LastReport = report; Event("app.waiting", state); }
        return state;
    }

    private void StopWaiting()
    {
        var wait = waiting;
        waiting = null;
        if (wait?.Started is not { } started) return;
        // An Android mirror nobody connected to is closed; one in use stays until the app is released.
        if (started == androidMirror) { if (sourceHandle == 0 && !connecting) StopAndroidMirror(); }
        else started.Dispose();
    }

    private async Task<object> ConnectApplication(nint handle, bool dock = true)
    {
        if (recorder is not null) throw new InvalidOperationException("Finish the current recording before choosing another app.");
        if (connecting) throw new InvalidOperationException("Studio is already connecting to a window.");
        connecting = true;
        try
        {
            var window = AppWindows.Find(handle) ?? throw new InvalidOperationException("That window is not open any more. Open it in the app, then choose it again.");
            var android = waiting is { Pid: not 0, Serial: { } serial } w && w.Pid == window.Pid ? serial : null;
            var keepMirror = android is not null ? androidMirror : null;
            if (keepMirror is not null) androidMirror = null; // survives the release of the previous source below
            ReleaseSource();
            androidMirror = keepMirror;
            // "Mirror only" leaves the app exactly where it is: never docked, moved or resized.
            var dockable = dock && window.Role == WindowRole.Main;
            if (dockable) appSurface!.Select(handle);
            if (window.Minimized) appSurface!.Unminimize();
            try { capture.Start(handle); }
            catch { appSurface!.Dispose(); throw; }
            StopWaiting();
            mirror = new MirrorCompositor(handle, (uint)window.Pid);
            mirror.Refresh();
            mirrorWatch.Start();
            sourceHandle = handle;
            sourceTitle = window.Title;
            // Store apps are reconnected by their app ID: UWP windows all belong to ApplicationFrameHost.exe. An
            // Android mirror is not reconnected: once it closes, it is launched again.
            sourcePath = android is not null ? null : window.AppId is { } appId ? AppCatalog.PackagePrefix + appId : window.Path;
            if (android is not null)
            {
                try { androidInput = new AndroidInput(android, handle, await AndroidTools.ScreenSize(android)); }
                catch (Exception error) { CrashLog.Write(root, error, "android input"); }
            }
            sourceRole = window.Role;
            sourcePid = window.Pid;
            bridgeToken = tokens.GetValueOrDefault(sourcePid);
            bridgePipe = "StudioBridge-" + sourcePid;
            object? bridge = null;
            if (bridgeToken is not null)
            {
                try { bridge = await Bridge(new JsonObject { ["action"] = "hello" }); }
                catch { bridgeToken = null; }
            }
            appView = dockable;
            appViewport = null;
            var result = new { state = "connected", title = sourceTitle, app = android is not null ? "Android" : window.AppId is not null || window.Path is null ? window.ProcessName : AppName(window.Path), handle = handle.ToString(), pid = sourcePid, role = dockable ? "main" : "floating", dockable, bridge, appView = dockable };
            connection = result;
            CrashLog.Info(root, $"connected '{sourceTitle}' pid={sourcePid} role={window.Role} size={window.Width}x{window.Height} minimized={window.Minimized}");
            Event("app.connected", result);
            return result;
        }
        finally { connecting = false; }
    }

    /// <summary>
    /// Records the whole display Studio is on. Unlike a window capture this includes the app's pop-ups, previews and
    /// menus; the editor's Frame tool then shows just the part that matters.
    /// </summary>
    private object ConnectScreen()
    {
        if (recorder is not null) throw new InvalidOperationException("Finish the current recording first.");
        StopWaiting();
        ReleaseSource();
        capture.StartMonitor(MonitorFromWindow(new WindowInteropHelper(this).Handle, 2));
        screenCapture = true;
        sourceTitle = "Whole screen";
        sourceRole = WindowRole.Floating;
        var result = new { state = "connected", title = sourceTitle, app = "Screen", handle = "screen", pid = 0, role = "floating", dockable = false, bridge = (object?)null, appView = false };
        connection = result;
        CrashLog.Info(root, "connected whole screen");
        Event("app.connected", result);
        return result;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);

    /// <summary>Stops capture and hands the window back to the desktop exactly as the app had it.</summary>
    private void ReleaseSource()
    {
        appView = false;
        try { appSurface?.Dispose(); }
        finally
        {
            capture.Dispose();
            mirrorWatch.Stop();
            mirror?.Dispose();
            mirror = null;
            androidInput?.Dispose();
            androidInput = null;
            StopAndroidMirror();
            connection = null;
            screenCapture = false;
            sourceHandle = 0;
            sourcePid = 0;
            sourcePath = null;
            bridgeToken = null;
        }
    }

    private void OnSourceLost(string message)
    {
        if (sourceHandle == 0) return;
        var path = sourcePath;
        var title = sourceTitle;
        try { ReleaseSource(); } catch (Exception error) { Trace.WriteLine(error); }
        var reconnect = path is not null && recorder is null;
        Event("app.closed", new { message = reconnect ? message + " Open it again from the app and Studio will reconnect." : message, reconnect });
        if (reconnect)
        {
            waiting = new Wait { Path = path!, Name = RecentName(path!), AlreadyRunning = true, PreferredTitle = title };
            _ = WaitingState(force: true);
        }
    }

    private object SetAppView(JsonObject data)
    {
        var mode = data["mode"]?.GetValue<string>() ?? "";
        if (mode is not ("app" or "3d")) throw new InvalidDataException("Unknown view.");
        if (mode == "app")
        {
            if (sourceHandle == 0 || appSurface?.Alive != true)
                throw new InvalidOperationException(sourceHandle != 0 && sourceRole == WindowRole.Floating
                    ? "Floating windows stay where the app puts them. Operate it on your desktop; Studio keeps capturing it."
                    : "Launch or choose your app first.");
        }
        appView = mode == "app";
        CrashLog.Info(root, $"view {mode}");
        // The app stays hosted in both views: in front for App view, tucked behind Studio for 3D view.
        if (!appView) Activate();
        PlaceAppSurface();
        if (!appView && appSurface?.IsDocked != true) appSurface?.Suspend();
        return new { mode, title = sourceTitle };
    }

    private void UpdateAppViewport(JsonObject data)
    {
        double Number(string name)
        {
            var value = data[name]?.GetValue<double>() ?? double.NaN;
            if (!double.IsFinite(value) || value < 0 || value > 20000) throw new InvalidDataException("Invalid app viewport.");
            return value;
        }
        var rect = new Rect(Number("x"), Number("y"), Number("width"), Number("height"));
        var zoom = Browser.ZoomFactor;
        if (rect.Right * zoom > Browser.ActualWidth + 2 || rect.Bottom * zoom > Browser.ActualHeight + 2)
            throw new InvalidDataException("The app viewport must stay inside Studio.");
        appViewport = rect;
        CrashLog.Info(root, $"viewport css={rect} zoom={Browser.ZoomFactor} studio={WindowState}");
        PlaceAppSurface();
    }

    private void PlaceAppSurface()
    {
        if (placingApp || appViewport is not Rect rect || appSurface?.Alive != true || sourceRole != WindowRole.Main || WindowState == WindowState.Minimized || !IsVisible) return;
        placingApp = true;
        try
        {
            var zoom = Browser.ZoomFactor;
            var topLeft = Browser.PointToScreen(new Point(rect.X * zoom, rect.Y * zoom));
            var bottomRight = Browser.PointToScreen(new Point(rect.Right * zoom, rect.Bottom * zoom));
            appSurface.Place(new Rect(topLeft, bottomRight), appView, new WindowInteropHelper(this).Handle);
        }
        catch (Exception error)
        {
            CrashLog.Write(root, error, "app view placement");
            appView = false;
            ReleaseQuietly();
            Event("app.viewError", new { message = error.Message });
        }
        finally { placingApp = false; }
    }

    private static string AppName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = string.IsNullOrWhiteSpace(info.ProductName) ? info.FileDescription : info.ProductName;
            // Use the short name before any tagline ("FlyShelf — Seamless …" → "FlyShelf"). Some apps' version
            // resources also carry a mis-encoded dash ("â€”"), which this drops along with the tagline.
            if (!string.IsNullOrWhiteSpace(name)) return name.Split([" — ", " – ", " - ", " â€", " | "], 2, StringSplitOptions.None)[0].Trim();
        }
        catch (FileNotFoundException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private string RecentFile => Path.Combine(root, "recent-apps.json");
    private string ToolsFile => Path.Combine(root, "tools.json");

    /// <summary>Tool locations the user chose in Settings (Blender, scrcpy) are kept between sessions.</summary>
    private void LoadToolPaths()
    {
        try
        {
            var tools = JsonNode.Parse(File.ReadAllText(ToolsFile));
            if (tools?["blender"]?.GetValue<string>() is { } blender && File.Exists(blender)) media.BlenderPath = blender;
            if (tools?["scrcpy"]?.GetValue<string>() is { } scrcpy && File.Exists(scrcpy)) AndroidTools.ScrcpyOverride = scrcpy;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private void SaveToolPaths()
    {
        try { File.WriteAllText(ToolsFile, JsonSerializer.Serialize(new { blender = media.BlenderPath, scrcpy = AndroidTools.ScrcpyOverride })); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
    }

    private async Task<object> AndroidStatus()
    {
        var adb = AndroidTools.Adb;
        List<AndroidDevice> devices = [];
        string? problem = null;
        if (adb is not null)
        {
            try { devices = await AndroidTools.Devices(); }
            catch (Exception error) when (error is IOException or TimeoutException or System.ComponentModel.Win32Exception) { problem = error.Message.Trim(); }
        }
        return new
        {
            adb, scrcpy = AndroidTools.Scrcpy, emulator = AndroidTools.Emulator, problem,
            devices = devices.Select(d => new { serial = d.Serial, state = d.State, model = d.Model, emulator = d.Emulator }),
            virtualDevices = await AndroidTools.VirtualDevices(),
        };
    }

    private sealed record RecentApp(string Path, string Name);

    /// <summary>Recently launched targets. Older Studio versions stored bare .exe paths, which still load.</summary>
    private List<RecentApp> RecentApps()
    {
        try
        {
            var list = new List<RecentApp>();
            foreach (var item in JsonNode.Parse(File.ReadAllText(RecentFile)) as JsonArray ?? [])
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var path)) list.Add(new RecentApp(path, AppName(path)));
                else if (item?["path"]?.GetValue<string>() is { Length: > 0 } key) list.Add(new RecentApp(key, item["name"]?.GetValue<string>() ?? key));
            }
            return list;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return []; }
    }

    private string RecentName(string target) => RecentApps().FirstOrDefault(a => a.Path == target)?.Name ?? (File.Exists(target) ? AppName(target) : target);

    private void RememberApp(string target, string name)
    {
        var list = RecentApps().Where(a => !(a.Path == target || WindowRules.SamePath(a.Path, target))).Prepend(new RecentApp(target, name)).Take(8).ToList();
        try { File.WriteAllText(RecentFile, JsonSerializer.Serialize(list.Select(a => new { path = a.Path, name = a.Name }))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
    }

    private static string Kind(string target) => target.StartsWith(AppCatalog.PackagePrefix, StringComparison.Ordinal) ? "store" : target.StartsWith(AppCatalog.AndroidPrefix, StringComparison.Ordinal) ? "android" : "desktop";

    private object[] DescribeRecentApps() => RecentApps().Where(a => Kind(a.Path) != "desktop" || File.Exists(a.Path))
        .Select(a => (object)new { path = a.Path, name = a.Name, kind = Kind(a.Path), running = Kind(a.Path) == "desktop" && AppWindows.IsRunning(a.Path) }).ToArray();

    private Task<object> LaunchRecent(JsonObject data)
    {
        var path = data["path"]?.GetValue<string>() ?? "";
        var dock = data["mirror"]?.GetValue<bool>() != true;
        // The editor may only relaunch apps the user picked in Studio before.
        var recent = RecentApps().FirstOrDefault(a => a.Path == path || WindowRules.SamePath(a.Path, path)) ?? throw new InvalidOperationException("Choose the app with Launch app first.");
        switch (Kind(recent.Path))
        {
            case "store": return LaunchPackage(recent.Path[AppCatalog.PackagePrefix.Length..], recent.Name, dock);
            case "android":
            {
                var rest = recent.Path[AppCatalog.AndroidPrefix.Length..];
                var slash = rest.LastIndexOf('/');
                if (slash <= 0) throw new InvalidDataException("Invalid recent app.");
                var model = recent.Name.Contains(" · ") ? recent.Name[(recent.Name.IndexOf(" · ", StringComparison.Ordinal) + 3)..] : null;
                return LaunchAndroid(rest[..slash], rest[(slash + 1)..], model, dock);
            }
            default: return LaunchApplication(recent.Path, dock);
        }
    }

    /// <summary>Starts an app picked from the installed-apps list. Only IDs that list contains are accepted.</summary>
    private async Task<object> LaunchInstalled(JsonObject data)
    {
        var id = data["id"]?.GetValue<string>() ?? "";
        var dock = data["mirror"]?.GetValue<bool>() != true;
        var app = (await AppCatalog.Installed()).FirstOrDefault(a => a.Id == id) ?? (await AppCatalog.Installed(refresh: true)).FirstOrDefault(a => a.Id == id)
            ?? throw new InvalidOperationException("That app is not installed any more.");
        return app.Kind == "store" ? await LaunchPackage(app.Id[AppCatalog.PackagePrefix.Length..], app.Name, dock) : await LaunchApplication(app.Target!, dock);
    }
}
