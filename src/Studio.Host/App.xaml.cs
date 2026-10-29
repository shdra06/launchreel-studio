using System.Security.Cryptography;
using System.Text;
using System.Windows;
namespace Studio.Host;

public partial class App : Application
{
    private static Mutex? instance;

    public static string DataRoot(string[] args)
    {
        var option = Array.IndexOf(args, "--data-root");
        if (option >= 0 && option + 1 < args.Length) return Path.GetFullPath(args[option + 1]);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Studio was called FlyShelf Ad Studio before 0.3: keep using that folder, with its projects and assets, if it exists.
        var current = Path.Combine(local, "LaunchReel");
        var legacy = Path.Combine(local, "FlyShelfAdStudio");
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Two copies sharing one data folder would overwrite each other's autosave and assets.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataRoot(Environment.GetCommandLineArgs()).ToUpperInvariant())))[..32];
        instance = new Mutex(true, "LaunchReelStudio-" + key, out var first);
        if (!first)
        {
            MessageBox.Show("LaunchReel Studio is already open. Use the window that is already running.", "LaunchReel Studio");
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
}

/// <summary>Plain-text error log in the Studio data folder (logs\studio-YYYY-MM-DD.log). Never throws.</summary>
internal static class CrashLog
{
    private static readonly object Gate = new();
    public static void Info(string root, string message) => Write(root, null, message);
    public static void Write(string root, Exception? error, string context)
    {
        try
        {
            var folder = Path.Combine(root, "logs");
            Directory.CreateDirectory(folder);
            lock (Gate) File.AppendAllText(Path.Combine(folder, $"studio-{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTime.Now:O} [{context}] {error}{Environment.NewLine}");
        }
        catch { }
    }
}
