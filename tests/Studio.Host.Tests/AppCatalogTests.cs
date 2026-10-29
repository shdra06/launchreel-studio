using Studio.Host;
using Xunit;
using Xunit.Abstractions;

namespace Studio.Host.Tests;

public class AppCatalogTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", true)]
    [InlineData(@"C:\Program Files\App\app.exe", false)]
    [InlineData("Microsoft.Office.WINWORD.EXE.15", false)]
    [InlineData("!App", false)]
    [InlineData("Family!", false)]
    [InlineData(@"..\evil!App", false)]
    public void Recognises_packaged_app_ids(string id, bool expected) => Assert.Equal(expected, AppCatalog.IsPackageId(id));

    [Theory]
    [InlineData("com.whatsapp")]
    [InlineData("com.android.settings")]
    [InlineData("org.mozilla.firefox_beta")]
    public void Accepts_android_package_names(string package) => Assert.Equal(package, AndroidTools.Package(package));

    [Theory]
    [InlineData("com.app; reboot")]
    [InlineData("com.app && rm -rf /")]
    [InlineData("com.app'")]
    [InlineData("single")]
    [InlineData("")]
    public void Rejects_anything_that_is_not_a_package_name(string package) => Assert.Throws<InvalidDataException>(() => AndroidTools.Package(package));

    [Theory]
    [InlineData("emulator-5554", true)]
    [InlineData("192.168.1.20:5555", true)]
    [InlineData("R58M12ABCDE", true)]
    [InlineData("abc def", false)]
    [InlineData("x;y", false)]
    public void Validates_device_serials(string serial, bool valid)
    {
        if (valid) Assert.Equal(serial, AndroidTools.Serial(serial));
        else Assert.Throws<InvalidDataException>(() => AndroidTools.Serial(serial));
    }

    /// <summary>Reads the Start menu only; launches nothing.</summary>
    [Fact]
    public async Task Lists_installed_apps_with_launchable_targets()
    {
        var apps = await AppCatalog.Installed(refresh: true);
        output.WriteLine($"{apps.Count} apps: {apps.Count(a => a.Kind == "store")} store, {apps.Count(a => a.Kind == "desktop")} desktop");
        foreach (var app in apps.Take(12)) output.WriteLine($"  {app.Kind,-8} {app.Name} -> {app.Target ?? app.Id}");
        Assert.All(apps, app => Assert.True(app.Kind == "store" ? AppCatalog.IsPackageId(app.Id[AppCatalog.PackagePrefix.Length..]) : File.Exists(app.Target)));
    }

    /// <summary>Reads open windows only.</summary>
    [Fact]
    public void Identifies_store_app_windows()
    {
        var windows = AppWindows.List();
        foreach (var w in windows.Where(w => w.AppId is not null)) output.WriteLine($"  {w.ProcessName} '{w.Title}' -> {w.AppId}");
        Assert.All(windows.Where(w => w.AppId is not null), w => Assert.Contains('!', w.AppId!));
    }
}

public class ShortcutGuardTests
{
    [Theory]
    [InlineData(new ushort[] { 0x12, 0x73 }, true)]  // Alt+F4
    [InlineData(new ushort[] { 0x12, 0x20 }, true)]  // Alt+Space (window menu)
    [InlineData(new ushort[] { 0x11, 0x57 }, true)]  // Ctrl+W
    [InlineData(new ushort[] { 0x11, 0x51 }, true)]  // Ctrl+Q
    [InlineData(new ushort[] { 0x11, 0x73 }, true)]  // Ctrl+F4
    [InlineData(new ushort[] { 0x11, 0x46 }, false)] // Ctrl+F
    [InlineData(new ushort[] { 0x0D }, false)]       // Enter
    public void Blocks_shortcuts_that_close_or_switch_apps(ushort[] keys, bool blocked) => Assert.Equal(blocked, DemoScript.ClosesOrSwitches(keys));
}
