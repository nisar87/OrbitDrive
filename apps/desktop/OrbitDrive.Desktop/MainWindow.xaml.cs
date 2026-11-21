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
    private readonly NotificationService _notices;
    private bool _quitting;

    public MainWindow(string stateDirectory)
    {
        InitializeComponent();
        Icon = AppIcon.Image;
        _host = new ConsoleHost(stateDirectory, Dispatcher);
        var updates = new UpdatePanel(_host.StateDirectory, _host.DrainForUpdateAsync, _host.QuitAsync);
        var home = new HomePageViewModel(_host, title => _shell!.GoTo(title), updates);
        var folders = new FoldersPageViewModel(_host, PickFolder, Confirm);
        var devices = new DevicesPageViewModel(_host, Confirm, CopyText);
        var activity = new ActivityPageViewModel(_host, Confirm, CopyText);
        var settings = new SettingsPageViewModel(_host, updates, CopyText);
        _shell = new ShellViewModel([
            new NavItem("\uE80F", home, "Ctrl+1"), new NavItem("\uE8B7", folders, "Ctrl+2"), new NavItem("\uE8EA", devices, "Ctrl+3"),
            new NavItem("\uE81C", activity, "Ctrl+4"), new NavItem("\uE713", settings, "Ctrl+5")]);
        DataContext = _shell;
        _shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) PageScroll.ScrollToTop(); };

        // Keyboard: Ctrl+1..5 jump straight to a section; Tab/arrow keys move through the rest in reading order.
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
        // A request to connect reaches the owner as a notification (toast, or a tray balloon) rather than a window that pops up by itself.
        _notices = new NotificationService(_host, Dispatcher, _tray, () => IsVisible && IsActive && WindowState != WindowState.Minimized, Review);
        _host.Quit += () =>
        {
            _quitting = true;
            _notices.Dispose();
            _tray.Dispose();
            Application.Current.Shutdown();
        };
        _host.Start();
    }

    private string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to share (read-only)" };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private static void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { /* another program holds the clipboard; the link stays selectable on the page */ }
    }

    private bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Review(string page) { Reopen(); _shell.GoTo(page); }

    internal void Reopen() { Show(); WindowState = WindowState.Normal; Activate(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting) { e.Cancel = true; Hide(); } // keep serving from the notification area
        base.OnClosing(e);
    }
}
