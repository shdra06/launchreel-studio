using System.Runtime.InteropServices;
using System.Text;

namespace Studio.Host;

/// <summary>An app from the Start menu. Desktop apps have an .exe <see cref="Target"/>; Store (packaged) apps an app ID.</summary>
public sealed record InstalledApp(string Id, string Name, string Kind, string? Target);

/// <summary>
/// The apps installed for this user, exactly as the Start menu lists them (shell:AppsFolder), and the two ways Studio
/// starts them: a desktop app through its .exe, a Store or packaged app (UWP, WinUI, MSIX) through Windows' own app
/// activation, which is what clicking its tile does. Nothing here changes an app or its settings.
/// </summary>
internal static class AppCatalog
{
    public const string PackagePrefix = "package:", AndroidPrefix = "android:";
    private static List<InstalledApp>? cached;
    private static DateTime cachedAt;

    /// <summary>The Start menu's app list. The shell's COM objects need an STA thread, so this runs on its own.</summary>
    public static Task<List<InstalledApp>> Installed(bool refresh = false)
    {
        if (!refresh && cached is not null && DateTime.UtcNow - cachedAt < TimeSpan.FromMinutes(2)) return Task.FromResult(cached);
        var done = new TaskCompletionSource<List<InstalledApp>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { cached = Enumerate(); cachedAt = DateTime.UtcNow; done.SetResult(cached); }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true, Name = "Installed apps" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static List<InstalledApp> Enumerate()
    {
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("The Windows shell is not available.");
        dynamic shell = Activator.CreateInstance(type)!;
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic items = shell.NameSpace("shell:AppsFolder").Items();
            int count = items.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    dynamic item = items.Item(i);
                    string name = item.Name ?? "", id = item.Path ?? "";
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || Unwanted(name)) continue;
                    InstalledApp? app = null;
                    if (IsPackageId(id)) app = new InstalledApp(PackagePrefix + id, name, "store", null);
                    else
                    {
                        // Desktop apps are listed through their Start menu shortcut; follow it to the program it starts.
                        string? target = null;
                        try { target = item.ExtendedProperty("System.Link.TargetParsingPath") as string; } catch (COMException) { }
                        if (string.IsNullOrEmpty(target) && Path.IsPathFullyQualified(id)) target = id;
                        if (target is null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                        if (Path.GetFileName(target).StartsWith("unins", StringComparison.OrdinalIgnoreCase) || WindowRules.SamePath(target, Environment.ProcessPath)) continue;
                        app = new InstalledApp(target, name, "desktop", target);
                    }
                    apps.TryAdd(app.Kind + "|" + app.Id, app);
                }
                catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool Unwanted(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("readme", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(" help", StringComparison.OrdinalIgnoreCase) || name.EndsWith(" documentation", StringComparison.OrdinalIgnoreCase);

    /// <summary>Packaged app IDs look like <c>PackageFamilyName!AppId</c>.</summary>
    public static bool IsPackageId(string id) => id.Length < 512 && id.IndexOf('!') is > 0 and var bang && bang < id.Length - 1 && !id.Contains('\\') && !id.Contains('/');

    /// <summary>Starts a packaged app the way its Start tile does and returns its process ID.</summary>
    public static uint Activate(string aumid)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            var result = manager.ActivateApplication(aumid, null, 0, out var pid);
            if (result == unchecked((int)0x800704C7)) throw new InvalidOperationException("The app was not started.");
            Marshal.ThrowExceptionForHR(result);
            return pid;
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    /// <summary>The packaged app ID of a running process, or null for ordinary desktop processes.</summary>
    public static string? ProcessAppId(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try
        {
            var length = 0u;
            if (GetApplicationUserModelId(process, ref length, null) != 122 || length is 0 or > 1024) return null;
            var buffer = new StringBuilder((int)length);
            return GetApplicationUserModelId(process, ref length, buffer) == 0 ? buffer.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    public static bool IsPackageRunning(string aumid)
    {
        foreach (var process in System.Diagnostics.Process.GetProcesses())
            using (process)
                if (string.Equals(ProcessAppId((uint)process.Id), aumid, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] private class ApplicationActivationManager { }

    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? id);
}
