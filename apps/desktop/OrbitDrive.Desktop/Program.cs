using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrbitDrive.Desktop;

/// <summary>
/// D0 feasibility window: proves a real WPF window can be launched, automated and captured on the CI runner.
/// The owner console (Overview, Shared Folders, Devices, ...) is phase D1; this window claims nothing more.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = BuildWindow();
        var exitAfter = Array.IndexOf(args, "--exit-after-ms");
        if (exitAfter >= 0 && exitAfter + 1 < args.Length && int.TryParse(args[exitAfter + 1], out var milliseconds))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => window.Close();
            timer.Start();
        }
        return app.Run(window);
    }

    private static TextBlock Text(string automationId, string text, double size, FontWeight weight, Brush brush)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = brush, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(block, automationId);
        return block;
    }

    private static Window BuildWindow()
    {
        var panel = new StackPanel { Margin = new Thickness(32) };
        panel.Children.Add(Text("ProductTitle", "OrbitDrive for Windows", 28, FontWeights.SemiBold, Brushes.White));
        panel.Children.Add(Text("ConnectionState", "Remote access: not configured", 16, FontWeights.Normal, new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66))));
        panel.Children.Add(Text("SharedFolders", "Shared folders: none. Nothing is shared until you choose a folder.", 14, FontWeights.Normal, Brushes.Gainsboro));
        panel.Children.Add(Text("RuntimeInfo", $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.FrameworkDescription}", 12, FontWeights.Normal, Brushes.Silver));
        var window = new Window
        {
            Title = "OrbitDrive",
            Width = 720,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new LinearGradientBrush(Color.FromRgb(0x10, 0x1B, 0x3A), Color.FromRgb(0x1F, 0x4E, 0x9E), 45),
            Content = panel,
            Topmost = true,
        };
        AutomationProperties.SetAutomationId(window, "OrbitDriveMainWindow");
        return window;
    }
}
