using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OrbitDrive.Core.Ipc;

namespace OrbitDrive.Desktop;

/// <summary>
/// Owner console. Everything shown is the agent's real state read over the local channel; the console holds
/// no sharing state of its own. Closing the window leaves the agent (and remote access) running; Quit ends both.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly string[] Pages = ["Overview", "Shared folders", "Devices", "Transfers", "Activity", "Settings", "Diagnostics"];
    private static readonly string[] PageIcons = ["\uE80F", "\uE8B7", "\uE8EA", "\uE896", "\uE81C", "\uE713", "\uE9D9"];
    // Theme brush keys (Windows Fluent theme): they follow light, dark and high-contrast automatically.
    private const string Muted = "TextFillColorSecondaryBrush";
    private const string Danger = "SystemFillColorCriticalBrush";

    private readonly string _stateDirectory;
    private readonly ListBox _nav = new() { Width = 210, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 14 };
    private readonly Grid _content = new();
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Windows.Forms.NotifyIcon _tray = new();
    private LocalIpcClient? _client;
    private readonly OrbitDrive.Core.RestartBudget _restarts = new(3, TimeSpan.FromMinutes(10), TimeProvider.System);
    private readonly OrbitDrive.Core.LockPolicy _lockPolicy = new();
    private readonly ConsoleSettings _settings;
    // _mayStart: the console may launch the agent on its next connection attempt (at launch, after a crash within the
    // restart budget, or when the owner presses Start). _stoppedOnPurpose: the agent announced a deliberate quit.
    private bool _quitting, _connecting, _paused, _mayStart = true, _stoppedOnPurpose, _gaveUp;
    private readonly System.Windows.Shapes.Ellipse _stateDot = new() { Width = 14, Height = 14, Margin = new Thickness(0, 4, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _start = MakeButton("StartAgentButton", "Start the OrbitDrive agent");
    private readonly TextBlock _statFolders = MakeStat("StatFolders"), _statTransfers = MakeStat("StatTransfers"), _statDevices = MakeStat("StatDevices");
    private readonly CheckBox _startAtSignIn = new() { Content = "Start OrbitDrive when I sign in to Windows", Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _pauseWhenLocked = new() { Content = "Pause remote access while this PC is locked", Margin = new Thickness(0, 4, 0, 4) };

    private readonly TextBlock _stateText = MakeText("StateText", "Starting…", 22, FontWeights.SemiBold);
    private readonly TextBlock _stateDetail = MakeText("StateDetail", "", 13, FontWeights.Normal, Muted);
    private readonly TextBlock _summary = MakeText("SummaryText", "", 14, FontWeights.Normal);
    private readonly Button _pause = MakeButton("PauseButton", "Pause remote access");
    private readonly ListBox _roots = new() { MinHeight = 120, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _rootsEmpty = MakeText("RootsEmpty", "Nothing is shared. Your files stay private until you add a folder here.", 13, FontWeights.Normal, Muted);
    private readonly TextBox _rootName = MakeBox("RootName"), _rootPath = MakeBox("RootPath"), _gateway = MakeBox("GatewayBox"), _webOrigin = MakeBox("WebOriginBox");
    private readonly TextBlock _rootError = MakeText("RootError", "", 13, FontWeights.Normal, Danger);
    private readonly TextBlock _pairCode = MakeText("PairCode", "", 26, FontWeights.SemiBold);
    private readonly TextBox _pairLink = new() { IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pairHelp = MakeText("PairHelp", "", 13, FontWeights.Normal, Muted);
    private readonly TextBlock _approvalText = MakeText("ApprovalText", "", 15, FontWeights.Normal);
    private readonly StackPanel _approvalPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 16, 0, 0) };
    private readonly TextBlock _deviceError = MakeText("DeviceError", "", 13, FontWeights.Normal, Danger);
    private readonly TextBlock _transfers = MakeText("TransfersText", "", 14, FontWeights.Normal);
    private readonly ListBox _activity = new() { MinHeight = 200 };
    private readonly TextBlock _settingsError = MakeText("SettingsError", "", 13, FontWeights.Normal, Danger);
    private readonly TextBlock _diagnostics = MakeText("DiagnosticsText", "", 13, FontWeights.Normal);
    private readonly Image _pairQr = new() { Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8), Visibility = Visibility.Collapsed };
    private readonly ListBox _devices = new() { MinHeight = 80, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _devicesEmpty = MakeText("DevicesEmpty", "No phone or browser is paired with this PC.", 13, FontWeights.Normal, Muted);
    private List<string> _rootIds = [];
    private List<string> _deviceIds = [];
    private string _qrFor = "";
    private int _tick;

    public MainWindow(string stateDirectory)
    {
        _stateDirectory = stateDirectory;
        _settings = new ConsoleSettings(stateDirectory);
        _lockPolicy.Enabled = _settings.PauseWhenLocked;
        Title = "OrbitDrive";
        Width = 980; Height = 680; MinWidth = 680; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Icon = AppIcon.Image;
        AutomationProperties.SetAutomationId(_startAtSignIn, "StartAtSignIn");
        AutomationProperties.SetAutomationId(_pauseWhenLocked, "PauseWhenLocked");
        AutomationProperties.SetAutomationId(this, "OrbitDriveMainWindow");
        AutomationProperties.SetAutomationId(_nav, "Navigation");
        AutomationProperties.SetAutomationId(_roots, "RootsList");
        AutomationProperties.SetAutomationId(_activity, "ActivityList");
        AutomationProperties.SetAutomationId(_pairLink, "PairLink");
        AutomationProperties.SetName(_nav, "Sections");
        AutomationProperties.SetAutomationId(_pairQr, "PairQr");
        AutomationProperties.SetName(_pairQr, "Pairing QR code");
        AutomationProperties.SetAutomationId(_devices, "DevicesList");
        RenderOptions.SetBitmapScalingMode(_pairQr, BitmapScalingMode.NearestNeighbor);

        BuildPages();
        for (var i = 0; i < Pages.Length; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = PageIcons[i], FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 30, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = Pages[i], VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = row, Tag = Pages[i], Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 1, 0, 1), ToolTip = $"{Pages[i]} (Ctrl+{i + 1})" };
            AutomationProperties.SetName(item, Pages[i]);
            _nav.Items.Add(item);
        }
        _nav.SelectionChanged += (_, _) =>
        {
            var selected = (_nav.SelectedItem as ListBoxItem)?.Tag as string;
            foreach (var (name, element) in _pages) element.Visibility = name == selected ? Visibility.Visible : Visibility.Collapsed;
        };
        _nav.SelectedIndex = 0;

        var layout = new DockPanel();
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4, 0, 18) };
        brand.Children.Add(new Image { Source = AppIcon.Image, Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0) });
        brand.Children.Add(new TextBlock { Text = "OrbitDrive", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var sidePanel = new DockPanel();
        DockPanel.SetDock(brand, Dock.Top);
        sidePanel.Children.Add(brand);
        sidePanel.Children.Add(_nav);
        var side = new Border { Padding = new Thickness(8, 16, 8, 16), Child = sidePanel, BorderThickness = new Thickness(0, 0, 1, 0) };
        side.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        side.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        DockPanel.SetDock(side, Dock.Left);
        layout.Children.Add(side);
        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(32, 24, 32, 24), Content = _content, Focusable = false });
        Content = layout;

        // Keyboard: Ctrl+1..7 jump straight to a section; Tab/arrow keys move through the rest in reading order.
        PreviewKeyDown += (_, e) =>
        {
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
            var index = e.Key - System.Windows.Input.Key.D1;
            if (index < 0 || index >= Pages.Length) index = e.Key - System.Windows.Input.Key.NumPad1;
            if (index < 0 || index >= Pages.Length) return;
            _nav.SelectedIndex = index;
            (_nav.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
            e.Handled = true;
        };

        _tray.Icon = AppIcon.Tray;
        _tray.Text = "OrbitDrive";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => Reopen();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open OrbitDrive", null, (_, _) => Reopen());
        menu.Items.Add("Pause / resume remote access", null, (_, _) => _ = TogglePauseAsync());
        menu.Items.Add("Quit OrbitDrive (stops remote access)", null, (_, _) => _ = QuitAsync());
        _tray.ContextMenuStrip = menu;

        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();                 // also when started hidden at sign-in (no window is shown, so no Loaded event)
        _ = RefreshAsync();
        Loaded += (_, _) => (_nav.ItemContainerGenerator.ContainerFromIndex(_nav.SelectedIndex) as ListBoxItem)?.Focus();
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>Windows lock/unlock. Applies the owner's "pause while locked" choice; never touches sleep settings.</summary>
    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (_quitting || _client is null) return;
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock && _lockPolicy.OnLocked(_paused))
        {
            LogActivity("PC locked: remote access paused");
            _ = RunAsync(_stateDetail, client => client.SendAsync("pause"));
        }
        else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock && _lockPolicy.OnUnlocked(_paused))
        {
            LogActivity("PC unlocked: remote access resumed");
            _ = RunAsync(_stateDetail, client => client.SendAsync("resume"));
        }
    });

    private void LogActivity(string line)
    {
        _activity.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (_activity.Items.Count > 200) _activity.Items.RemoveAt(_activity.Items.Count - 1); // bounded history
    }

    private static TextBlock MakeText(string id, string text, double size, FontWeight weight, string? brushKey = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        if (brushKey is not null) block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    private static TextBlock MakeStat(string id)
    {
        var block = new TextBlock { Text = "0", FontSize = 30, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    private static Button MakeButton(string id, string text, bool accent = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 34 };
        if (accent) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    /// <summary>A rounded surface that groups related controls, optionally under a heading.</summary>
    private static Border Card(string? heading, params UIElement[] children)
    {
        var panel = new StackPanel();
        if (heading is not null) panel.Children.Add(new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var child in children) panel.Children.Add(child);
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(20, 16, 20, 14), Margin = new Thickness(0, 0, 0, 14), BorderThickness = new Thickness(1), Child = panel };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private static Border Stat(TextBlock value, string label)
    {
        var caption = new TextBlock { Text = label, FontSize = 13 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        var card = Card(null, value, caption);
        card.Margin = new Thickness(0, 0, 14, 14);
        return card;
    }

    private static TextBox MakeBox(string id)
    {
        var box = new TextBox { Padding = new Thickness(6), Margin = new Thickness(0, 2, 0, 8), MinHeight = 32 };
        AutomationProperties.SetAutomationId(box, id);
        return box;
    }

    private static TextBlock MakeLabel(string text, TextBox target)
    {
        var label = new TextBlock { Text = text, FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, Muted);
        AutomationProperties.SetName(target, text);
        return label;
    }

    private StackPanel Page(string name, params UIElement[] children)
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };
        panel.Children.Add(new TextBlock { Text = name, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        foreach (var child in children) panel.Children.Add(child);
        _pages[name] = panel;
        _content.Children.Add(panel);
        return panel;
    }

    private void BuildPages()
    {
        var quit = MakeButton("QuitButton", "Quit OrbitDrive");
        quit.Click += (_, _) => _ = QuitAsync();
        _pause.Click += (_, _) => _ = TogglePauseAsync();
        _pause.SetResourceReference(StyleProperty, "AccentButtonStyle");
        // Start is offered only while the agent is not running; pressing it is an explicit owner decision, so it
        // also clears the crash-restart budget.
        _start.Visibility = Visibility.Collapsed;
        _start.Click += (_, _) => { _restarts.Reset(); _gaveUp = false; _stoppedOnPurpose = false; _mayStart = true; _ = RefreshAsync(); };
        _stateText.Margin = new Thickness(0);
        var stats = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -14, 0) };
        stats.Children.Add(Stat(_statFolders, "Shared folders"));
        stats.Children.Add(Stat(_statTransfers, "Transfers running"));
        stats.Children.Add(Stat(_statDevices, "Paired devices"));
        Page("Overview",
            Card(null, Row(_stateDot, _stateText), _stateDetail, Row(_pause, _start), _summary),
            stats,
            Card("When you close this window",
                MakeText("CloseHint", "Closing this window keeps OrbitDrive running in the notification area so your phone can still reach shared folders. Quit stops remote access.", 13, FontWeights.Normal, Muted), quit));

        var browse = MakeButton("BrowseButton", "Browse…");
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to share (read-only)" };
            if (dialog.ShowDialog(this) == true) { _rootPath.Text = dialog.FolderName; if (_rootName.Text.Length == 0) _rootName.Text = Path.GetFileName(dialog.FolderName.TrimEnd('\\')); }
        };
        var add = MakeButton("AddRootButton", "Share this folder (read-only)", accent: true);
        add.Click += (_, _) => _ = AddRootAsync();
        var remove = MakeButton("RemoveRootButton", "Stop sharing selected folder");
        remove.Click += (_, _) => _ = RunAsync(_rootError, async client =>
        {
            if (_roots.SelectedIndex < 0 || _roots.SelectedIndex >= _rootIds.Count) throw new IpcException("Select a folder first.");
            await client.SendAsync("remove_root", new JsonObject { ["rootId"] = _rootIds[_roots.SelectedIndex] });
        });
        Page("Shared folders",
            MakeText("RootsIntro", "Only folders listed here can be seen from your paired devices. Sharing is read-only, and Windows permissions still apply.", 13, FontWeights.Normal, Muted),
            Card("Shared now", _rootsEmpty, _roots, remove),
            Card("Share another folder", MakeLabel("Name shown on your phone", _rootName), _rootName, MakeLabel("Folder", _rootPath), _rootPath, Row(browse, add), _rootError));

        var pair = MakeButton("PairButton", "Pair a phone or browser", accent: true);
        pair.Click += (_, _) => _ = RunAsync(_deviceError, client => client.SendAsync("pair"));
        var approve = MakeButton("ApproveButton", "Approve", accent: true);
        approve.Click += (_, _) => _ = RunAsync(_deviceError, client => client.SendAsync("approve"));
        var deny = MakeButton("DenyButton", "Deny");
        deny.Click += (_, _) => _ = RunAsync(_deviceError, client => client.SendAsync("deny"));
        _approvalPanel.Margin = new Thickness(0);
        _approvalPanel.Children.Add(Card("A browser wants access", _approvalText, Row(approve, deny)));
        var revoke = MakeButton("RevokeDeviceButton", "Remove selected device's access");
        revoke.Click += (_, _) => _ = RunAsync(_deviceError, async client =>
        {
            if (_devices.SelectedIndex < 0 || _devices.SelectedIndex >= _deviceIds.Count) throw new IpcException("Select a device first.");
            await client.SendAsync("revoke_device", new JsonObject { ["deviceId"] = _deviceIds[_devices.SelectedIndex] });
            _tick = 0;
        });
        Page("Devices", _approvalPanel,
            Card("Pair a new device", pair, _pairQr, _pairCode, _pairLink, _pairHelp, _deviceError),
            Card("Paired devices", _devicesEmpty, _devices, revoke));

        Page("Transfers", Card(null, _transfers,
            MakeText("TransfersHint", "OrbitDrive can see what it has sent to the relay. It cannot see whether a browser finished saving a file.", 13, FontWeights.Normal, Muted)));
        Page("Activity", Card("This session", _activity));

        var save = MakeButton("SaveGatewayButton", "Save and connect", accent: true);
        save.Click += (_, _) => _ = RunAsync(_settingsError, client =>
            client.SendAsync("set_gateway", new JsonObject { ["gateway"] = _gateway.Text.Trim(), ["webOrigin"] = _webOrigin.Text.Trim().Length == 0 ? null : _webOrigin.Text.Trim() }));
        _startAtSignIn.IsChecked = StartupRegistration.IsEnabled(_stateDirectory);
        _startAtSignIn.Click += (_, _) =>
        {
            _settingsError.Text = "";
            try { StartupRegistration.Set(_stateDirectory, _startAtSignIn.IsChecked == true); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { _settingsError.Text = "Windows did not allow changing the start-up setting."; }
            _startAtSignIn.IsChecked = StartupRegistration.IsEnabled(_stateDirectory);
        };
        _pauseWhenLocked.IsChecked = _settings.PauseWhenLocked;
        _pauseWhenLocked.Click += (_, _) =>
        {
            _settingsError.Text = "";
            try { _settings.SetPauseWhenLocked(_pauseWhenLocked.IsChecked == true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _settingsError.Text = "The setting could not be saved."; }
            _lockPolicy.Enabled = _settings.PauseWhenLocked;
            _pauseWhenLocked.IsChecked = _settings.PauseWhenLocked;
        };
        Page("Settings",
            Card("Connection", MakeLabel("Relay address (https://…)", _gateway), _gateway, MakeLabel("Web app address shown in pairing links (optional)", _webOrigin), _webOrigin, save),
            Card("This PC", _startAtSignIn,
                MakeText("StartHint", "It starts quietly in the notification area, as you, without administrator rights. Off unless you turn it on.", 13, FontWeights.Normal, Muted),
                _pauseWhenLocked,
                MakeText("SleepNote", "While this PC is locked OrbitDrive keeps working unless you tick the box above. When the PC sleeps, shuts down or you sign out, your phone cannot reach it. OrbitDrive never changes your sleep settings or keeps the PC awake.", 13, FontWeights.Normal, Muted)),
            _settingsError,
            MakeText("PrivacyNote", "Files travel over TLS through the relay, which can process them in transit and stores nothing. This is not end-to-end encryption.", 13, FontWeights.Normal, Muted));
        Page("Diagnostics", Card(null, _diagnostics));
    }

    private async Task<LocalIpcClient?> ConnectAsync()
    {
        if (_client is not null) return _client;
        if (_connecting) return null;
        _connecting = true;
        try
        {
            var name = LocalIpc.PipeName(_stateDirectory);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var client = await LocalIpcClient.ConnectAsync(name, TimeSpan.FromSeconds(attempt == 0 ? 1 : 10));
                    client.EventReceived += (eventName, data) => Dispatcher.InvokeAsync(() => OnEvent(eventName, data));
                    client.Disconnected += () => Dispatcher.InvokeAsync(() => OnAgentGone(client));
                    _mayStart = false; _stoppedOnPurpose = false; _gaveUp = false;
                    return _client = client;
                }
                catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    // The agent is launched only when explicitly permitted (see _mayStart), never in an open loop.
                    if (attempt != 0 || !_mayStart || !Program.StartAgent(_stateDirectory)) break;
                    _mayStart = false;
                }
            }
            return null;
        }
        finally { _connecting = false; }
    }

    /// <summary>
    /// The agent's channel closed. A deliberate stop (Quit here, or the agent announcing "quitting") is respected; an
    /// unexpected exit is recovered automatically, but only a bounded number of times so a crash loop cannot spin.
    /// </summary>
    private void OnAgentGone(LocalIpcClient client)
    {
        if (!ReferenceEquals(_client, client)) return;
        _client = null;
        if (_quitting || _stoppedOnPurpose) return;
        if (_restarts.TryConsume())
        {
            _mayStart = true;
            LogActivity($"The agent stopped unexpectedly; restarting it ({_restarts.Used} of {_restarts.MaxRestarts})");
        }
        else
        {
            _gaveUp = true;
            LogActivity("The agent keeps stopping; it was not restarted again");
        }
        _ = RefreshAsync();
    }

    private void OnEvent(string name, JsonNode? data)
    {
        if (name == "quitting") _stoppedOnPurpose = true;
        var line = name switch
        {
            "connected" => "Connected to the relay",
            "disconnected" => "Connection to the relay lost; retrying",
            "paused" => "Remote access paused",
            "resumed" => "Remote access resumed",
            "pairing" => "Pairing code created",
            "pairing_claimed" => $"{data?["browserName"]} asked to pair",
            "pairing_result" => $"Pairing {data?["state"]}",
            "roots_changed" => "Shared folders changed",
            "device_revoked" => "A device's access was removed",
            "stream_finished" => $"Transfer {data?["outcome"]} ({data?["bytes"]} bytes sent to the relay)",
            "update_required" => "This version is too old for the service",
            "quitting" => "The agent was stopped",
            _ => name,
        };
        LogActivity(line);
        if (name is "pairing_result" or "device_revoked") _tick = 0;
        if (name == "pairing_claimed" && !IsVisible) Reopen(); // an approval needs the owner's attention
        _ = RefreshAsync();
    }

    private async Task RunAsync(TextBlock errorTarget, Func<LocalIpcClient, Task> action)
    {
        errorTarget.Text = "";
        try
        {
            var client = await ConnectAsync() ?? throw new IpcException("The OrbitDrive agent is not running.");
            await action(client);
        }
        catch (IpcException e) { errorTarget.Text = e.Message; }
        catch (TimeoutException) { errorTarget.Text = "The agent did not answer in time."; }
        await RefreshAsync();
    }

    private Task AddRootAsync() => RunAsync(_rootError, async client =>
    {
        var path = _rootPath.Text.Trim();
        if (path.Length > 0 && Path.GetPathRoot(path) == path &&
            MessageBox.Show(this, $"Share the whole drive {path}?\n\nEvery folder on it that your Windows account can read becomes visible to your paired devices.", "Share an entire drive", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        await client.SendAsync("add_root", new JsonObject { ["name"] = _rootName.Text.Trim(), ["path"] = path });
        _rootName.Text = ""; _rootPath.Text = "";
    });

    private Task TogglePauseAsync()
    {
        _lockPolicy.OnOwnerChangedPause();
        return RunAsync(_stateDetail, client => client.SendAsync(_paused ? "resume" : "pause"));
    }

    private void SetDot(byte r, byte g, byte b) => _stateDot.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));

    private async Task RefreshAsync()
    {
        if (_quitting) return;
        JsonObject? status = null;
        try { if (await ConnectAsync() is { } client) status = await client.SendAsync("status") as JsonObject; }
        catch (Exception e) when (e is IpcException or TimeoutException) { }
        if (status is null)
        {
            (_stateText.Text, _stateDetail.Text) =
                _gaveUp ? ("Agent keeps stopping", $"OrbitDrive's background agent stopped unexpectedly {_restarts.MaxRestarts + 1} times in a short while, so it was not started again. Nothing is being shared.")
                : _stoppedOnPurpose ? ("Stopped", "The OrbitDrive agent was stopped. Nothing is being shared until you start it again.")
                : _mayStart ? ("Starting…", "Starting OrbitDrive's background agent.")
                : ("Agent not running", "OrbitDrive's background agent is not running. Nothing is being shared.");
            SetDot(0xC4, 0x2B, 0x1C);
            _pause.IsEnabled = false;
            _pause.Visibility = Visibility.Collapsed;
            _start.Visibility = _mayStart ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        _pause.Visibility = Visibility.Visible;
        _start.Visibility = Visibility.Collapsed;
        var state = status["state"]!.GetValue<string>();
        _paused = status["paused"]!.GetValue<bool>();
        (_stateText.Text, _stateDetail.Text) = state switch
        {
            "online" => ("Online", "Paired devices can browse your shared folders."),
            "paused" => ("Paused", "Remote access is paused. Paired devices cannot see anything until you resume."),
            "connecting" => ("Connecting…", "Trying to reach the relay. Remote access is unavailable until connected."),
            "update_required" => ("Update required", "This version of OrbitDrive is no longer accepted by the service."),
            "not_configured" => ("Not set up", "No relay address is set. Open Settings to connect this PC."),
            _ => ("Disconnected", "Not connected."),
        };
        switch (state)
        {
            case "online": SetDot(0x0F, 0x7B, 0x0F); break;
            case "connecting": SetDot(0xC1, 0x7D, 0x00); break;
            case "update_required": SetDot(0xC4, 0x2B, 0x1C); break;
            default: SetDot(0x8A, 0x8A, 0x8A); break;
        }
        _pause.IsEnabled = true;
        _pause.Content = _paused ? "Resume remote access" : "Pause remote access";

        var roots = status["roots"]!.AsArray();
        _summary.Text = $"This PC appears as “{status["pcName"]}” on your devices.";
        _statFolders.Text = roots.Count.ToString();
        _statTransfers.Text = ((int)status["activeStreams"]!).ToString();
        var ids = roots.Select(r => r!["rootId"]!.GetValue<string>()).ToList();
        if (!ids.SequenceEqual(_rootIds))
        {
            _rootIds = ids;
            _roots.Items.Clear();
            foreach (var root in roots)
                _roots.Items.Add($"{root!["name"]}  —  {root["path"]}  (read-only{((bool)root["available"]! ? "" : ", currently unavailable")})");
        }
        _rootsEmpty.Visibility = roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var pairing = status["activePairing"];
        _pairCode.Text = pairing?["code"]?.GetValue<string>() ?? "";
        _pairLink.Text = pairing?["url"]?.GetValue<string>() ?? "";
        _pairHelp.Text = pairing is null ? "Create a one-time code, then open the link (or type the code) in the OrbitDrive web app on your phone. Codes expire after two minutes."
            : "Open this link on your phone, or type the code in the OrbitDrive web app. It works once and expires in two minutes. Only use a code you created yourself just now.";
        var link = pairing?["url"]?.GetValue<string>() ?? "";
        if (link != _qrFor)
        {
            _qrFor = link;
            _pairQr.Source = link.Length == 0 ? null : RenderQr(link);
            _pairQr.Visibility = link.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        if (state is "online" or "paused" && _tick++ % 5 == 0)
        {
            try
            {
                var list = _client is null ? new JsonArray() : (await _client.SendAsync("devices") as JsonArray) ?? new JsonArray();
                var deviceIds = list.Select(d => d!["deviceId"]!.GetValue<string>()).ToList();
                if (!deviceIds.SequenceEqual(_deviceIds))
                {
                    _deviceIds = deviceIds;
                    _devices.Items.Clear();
                    foreach (var device in list) _devices.Items.Add(device!["name"]!.GetValue<string>());
                }
                _devicesEmpty.Visibility = deviceIds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                _statDevices.Text = deviceIds.Count.ToString();
            }
            catch (Exception e) when (e is IpcException or TimeoutException) { }
        }
        var approval = status["pendingApproval"];
        _approvalPanel.Visibility = approval is null ? Visibility.Collapsed : Visibility.Visible;
        _approvalText.Text = approval is null ? "" : $"Account: {approval["accountLabel"]}\nBrowser: {approval["browserName"]}\nCode: {approval["confirmationCode"]}\n\nApprove only if this is you and the same code is shown in your browser.";

        _transfers.Text = (int)status["activeStreams"]! == 0 ? "No transfers are running." : $"{status["activeStreams"]} file(s) are being sent to the relay right now.";
        if (!_gateway.IsKeyboardFocused && _gateway.Text.Length == 0) _gateway.Text = status["gateway"]?.GetValue<string>() ?? "";
        _diagnostics.Text = $"PC id: {status["pcId"]}\nAgent version: {status["agentVersion"]}\nRelay: {status["gateway"] ?? "(not set)"}\nState folder: {_stateDirectory}\n" +
                            $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.FrameworkDescription}\nRunning elevated: {Environment.IsPrivilegedProcess}\n" +
                            $"Theme: {Program.ThemeChoice}{(SystemParameters.HighContrast ? " (Windows high contrast is on)" : "")}\nAutomatic agent restarts used: {_restarts.Used} of {_restarts.MaxRestarts} (per 10 minutes)";
    }

    /// <summary>Draws the pairing link as a QR code with the 4-module quiet zone scanners need.</summary>
    private static System.Windows.Media.Imaging.BitmapSource RenderQr(string text)
    {
        var qr = OrbitDrive.Core.QrCode.Encode(text);
        const int quiet = 4;
        var side = qr.Size + quiet * 2;
        var pixels = new byte[side * side];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < qr.Size; y++)
            for (var x = 0; x < qr.Size; x++)
                if (qr[x, y]) pixels[(y + quiet) * side + x + quiet] = 0;
        return System.Windows.Media.Imaging.BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, pixels, side);
    }

    internal void Reopen() { Show(); WindowState = WindowState.Normal; Activate(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting) { e.Cancel = true; Hide(); } // keep serving from the notification area
        base.OnClosing(e);
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _timer.Stop();
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        try { if (_client is not null) await _client.SendAsync("quit"); } catch (Exception e) when (e is IpcException or TimeoutException) { }
        _tray.Visible = false;
        _tray.Dispose();
        Application.Current.Shutdown();
    }
}
