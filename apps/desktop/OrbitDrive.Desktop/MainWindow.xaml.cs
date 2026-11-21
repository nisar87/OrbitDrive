using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrbitDrive.Desktop.ViewModels;

namespace OrbitDrive.Desktop;

/// <summary>
/// The console window: navigation and page hosting. View concerns only; the engine is <see cref="ConsoleHost"/>, the pages are view models.
/// Closing the window leaves the agent (and remote access) running; Quit ends both.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const double CompactBelow = 860;
    private readonly ConsoleHost _host;
    private readonly ShellViewModel _shell;
    private readonly TrayService _tray;
    private bool _quitting;

    public MainWindow(string stateDirectory)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        _host = new ConsoleHost(stateDirectory, Dispatcher);
        var legacy = new LegacyConsole(_host, this);

        string[] names = ["Overview", "Shared folders", "Devices", "Transfers", "Activity", "Settings", "Diagnostics"];
        string[] glyphs = ["", "", "", "", "", "", ""];
        _shell = new ShellViewModel(names.Select((name, i) => new NavItem(glyphs[i], new LegacyPageViewModel(name, legacy.Pages[name]), $"Ctrl+{i + 1}")));
        legacy.CurrentPageName = () => _shell.SelectedTitle;
        DataContext = _shell;
        _shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) PageScroll.ScrollToTop(); };

        // Keyboard: Ctrl+1..7 jump straight to a section; Tab/arrow keys move through the rest in reading order.
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            var index = e.Key - Key.D1;
            if (index < 0 || index >= _shell.NavItems.Count) index = e.Key - Key.NumPad1;
            if (index < 0 || index >= _shell.NavItems.Count) return;
            _shell.SelectIndex(index);
            (Nav.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
            e.Handled = true;
        };
        SizeChanged += (_, _) => _shell.IsCompact = ActualWidth < CompactBelow;
        Loaded += (_, _) => (Nav.ItemContainerGenerator.ContainerFromIndex(Nav.SelectedIndex) as ListBoxItem)?.Focus();

        _tray = new TrayService(Reopen, () => _ = _host.TogglePauseAsync(), () => _ = _host.QuitAsync());
        _host.AttentionNeeded += () => { if (!IsVisible) Reopen(); };   // an approval needs the owner's attention
        _host.Quit += () =>
        {
            _quitting = true;
            _tray.Dispose();
            Application.Current.Shutdown();
        };
        _host.Start();
    }

    internal void Reopen() { Show(); WindowState = WindowState.Normal; Activate(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting) { e.Cancel = true; Hide(); } // keep serving from the notification area
        base.OnClosing(e);
    }
}
