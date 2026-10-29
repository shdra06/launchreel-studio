using System.Windows;
using System.Windows.Threading;
using LaunchReel.Studio;
namespace Studio.Fixture;

/// <summary>
/// Behaves like a tray app: closing hides the window instead of exiting. Command-line switches let the automated
/// App view check reproduce awkward real-world behaviour without touching any real application.
/// </summary>
public partial class MainWindow : Window
{
    private StudioBridge? bridge;
    private int count;
    public MainWindow()
    {
        InitializeComponent();
        // Start mostly off the primary screen so automated checks barely appear on the desktop.
        Left = SystemParameters.PrimaryScreenWidth - 24; Top = SystemParameters.PrimaryScreenHeight - 160;
        Loaded += (_, _) =>
        {
            var token = Environment.GetEnvironmentVariable("STUDIO_BRIDGE_TOKEN");
            if (token is not null) { bridge = new(this, token); bridge.Register("field", "Text field", Field); bridge.Register("panel", "Output panel", Panel); }
        };
        Closed += (_, _) => bridge?.Dispose();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Output.Text = $"Ready · {count} · {DateTime.Now:HH:mm:ss.f}";
        timer.Start();
        // Safety net: a fixture left behind by an interrupted check never lingers.
        new DispatcherTimer(TimeSpan.FromMinutes(4), DispatcherPriority.Normal, (_, _) => Application.Current.Shutdown(), Dispatcher).Start();
    }
    private void Count(object sender, RoutedEventArgs e) => count++;
    private void HideSelf(object sender, RoutedEventArgs e) => Hide();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
}
