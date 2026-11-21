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

        string[] legacyNames = ["Settings", "Diagnostics"];
        string[] legacyGlyphs = ["\uE713", "\uE9D9"];
        var home = new HomePageViewModel(_host, title => _shell!.GoTo(title), legacy.Updates);
        var folders = new FoldersPageViewModel(_host, PickFolder, Confirm);
        var devices = new DevicesPageViewModel(_host, Confirm, CopyText);
        var activity = new ActivityPageViewModel(_host, Confirm, CopyText);
        var items = new List<NavItem> { new("\uE80F", home, "Ctrl+1"), new("\uE8B7", folders, "Ctrl+2"), new("\uE8EA", devices, "Ctrl+3"), new("\uE81C", activity, "Ctrl+4") };
        items.AddRange(legacyNames.Select((name, i) => new NavItem(legacyGlyphs[i], new LegacyPageViewModel(name, legacy.Pages[name]), $"Ctrl+{i + 5}")));
        _shell = new ShellViewModel(items);
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

    internal void Reopen() { Show(); WindowState = WindowState.Normal; Activate(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting) { e.Cancel = true; Hide(); } // keep serving from the notification area
        base.OnClosing(e);
    }
}
