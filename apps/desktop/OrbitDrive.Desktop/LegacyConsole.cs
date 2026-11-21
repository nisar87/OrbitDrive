using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using OrbitDrive.Core.Ipc;

namespace OrbitDrive.Desktop;

/// <summary>
/// TRANSITIONAL. The seven pages as they were before the redesign, hosted inside the new shell so the console keeps working and the UI tests stay green while
/// the pages are rebuilt one at a time as XAML + view models. Each page that is replaced is deleted from here; when the last one goes, so does this file.
/// Everything shown is the agent's real state read over the local channel.
/// </summary>
internal sealed class LegacyConsole
{

    // Theme brush keys (Windows Fluent theme): they follow light, dark and high-contrast automatically.
    // Secondary text: the theme's own secondary brush measured about 3.2:1 on cards in real light-theme screenshots,
    // below the 4.5:1 needed for small text, so secondary text is the primary colour at reduced opacity (about 5.5:1
    // light, 7:1 dark) and full strength in Windows high contrast.
    internal const string Muted = "muted";
    internal static void Mute(TextBlock block) => block.Opacity = SystemParameters.HighContrast ? 1.0 : 0.74;
    internal const string Danger = "SystemFillColorCriticalBrush";

    private readonly ConsoleHost _host;
    private readonly Window _owner;
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private bool _paused;
    /// <summary>The shell tells this which page is on screen, so Activity and Settings are refreshed only while visible.</summary>
    public Func<string?> CurrentPageName { get; set; } = () => null;
    public IReadOnlyDictionary<string, FrameworkElement> Pages => _pages;

    private readonly System.Windows.Shapes.Ellipse _stateDot = new() { Width = 14, Height = 14, Margin = new Thickness(0, 4, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _start = MakeButton("StartAgentButton", "Start the OrbitDrive agent");
    private readonly TextBlock _statFolders = MakeStat("StatFolders"), _statTransfers = MakeStat("StatTransfers"), _statDevices = MakeStat("StatDevices");
    private readonly CheckBox _startAtSignIn = new() { Content = "Start OrbitDrive when I sign in to Windows", Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _pauseOnMetered = new() { Content = "Pause remote access while this PC is on a metered connection", Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _meteredWarning = MakeText("MeteredWarning", "", 13, FontWeights.Normal, Danger);
    private readonly TextBox _maxSpeed = MakeBox("MaxSpeedBox"), _maxFiles = MakeBox("MaxFilesBox");
    private readonly TextBlock _limitsError = MakeText("LimitsError", "", 13, FontWeights.Normal, Danger);
    private string _limitsShown = "";
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
    private HistoryPanel _history = null!;
    private ThumbnailPanel _thumbnails = null!;
    private readonly TextBlock _settingsError = MakeText("SettingsError", "", 13, FontWeights.Normal, Danger);
    private readonly TextBlock _diagnostics = MakeText("DiagnosticsText", "", 13, FontWeights.Normal);
    private readonly Image _pairQr = new() { Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8), Visibility = Visibility.Collapsed };
    private readonly ListBox _devices = new() { MinHeight = 80, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _devicesEmpty = MakeText("DevicesEmpty", "No phone or browser is paired with this PC.", 13, FontWeights.Normal, Muted);
    private List<string> _rootIds = [];
    private List<bool> _rootHidden = [];
    private List<bool> _rootUploads = [];
    private readonly ListBox _transferList = new() { MinHeight = 0, Margin = new Thickness(0, 4, 0, 8) };
    private string _transfersShown = "";
    private string _rootsShown = "";
    private readonly CheckBox _newRootHidden = new() { Content = "Also show hidden files and folders from this folder", Margin = new Thickness(0, 0, 0, 6) };
    private List<string> _deviceIds = [];
    private string _qrFor = "";
    private int _tick;
    private UpdatePanel _updates = null!;


    public LegacyConsole(ConsoleHost host, Window owner)
    {
        _host = host;
        _owner = owner;
        AutomationProperties.SetAutomationId(_startAtSignIn, "StartAtSignIn");
        AutomationProperties.SetAutomationId(_pauseWhenLocked, "PauseWhenLocked");
        AutomationProperties.SetAutomationId(_pauseOnMetered, "PauseOnMetered");
        _meteredWarning.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_roots, "RootsList");
        ScrollViewer.SetHorizontalScrollBarVisibility(_roots, ScrollBarVisibility.Disabled);   // long paths wrap instead of scrolling sideways
        _roots.MinHeight = 0;
        AutomationProperties.SetAutomationId(_activity, "ActivityList");
        AutomationProperties.SetAutomationId(_pairLink, "PairLink");
        AutomationProperties.SetAutomationId(_pairQr, "PairQr");
        AutomationProperties.SetName(_pairQr, "Pairing QR code");
        AutomationProperties.SetAutomationId(_devices, "DevicesList");
        RenderOptions.SetBitmapScalingMode(_pairQr, BitmapScalingMode.NearestNeighbor);
        BuildPages();
        host.Note += LogActivity;
        host.AgentEvent += (name, _) => { if (name is "pairing_result" or "device_revoked") _tick = 0; };
        host.AddStatusConsumer(ApplyStatusAsync);
    }

    private void LogActivity(string line)
    {
        _activity.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (_activity.Items.Count > 200) _activity.Items.RemoveAt(_activity.Items.Count - 1); // bounded history
    }

    internal static TextBlock MakeText(string id, string text, double size, FontWeight weight, string? brushKey = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        if (brushKey == Muted) Mute(block);
        else if (brushKey is not null) block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    private static TextBlock MakeStat(string id)
    {
        var block = new TextBlock { Text = "0", FontSize = 30, FontWeight = FontWeights.SemiBold };
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    internal static Button MakeButton(string id, string text, bool accent = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 34 };
        if (accent) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    /// <summary>A rounded surface that groups related controls, optionally under a heading.</summary>
    internal static Border Card(string? heading, params UIElement[] children)
    {
        var panel = new StackPanel();
        if (heading is not null) panel.Children.Add(new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var child in children) panel.Children.Add(child);
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(20, 16, 20, 14), Margin = new Thickness(0, 0, 0, 14), BorderThickness = new Thickness(1), Child = panel };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    internal static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private static Border Stat(TextBlock value, string label)
    {
        var caption = new TextBlock { Text = label, FontSize = 13 };
        Mute(caption);
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
        Mute(label);
        AutomationProperties.SetName(target, text);
        return label;
    }

    private StackPanel Page(string name, params UIElement[] children)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = name, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        foreach (var child in children) panel.Children.Add(child);
        _pages[name] = panel;
        return panel;
    }

    private void BuildPages()
    {
        _updates = new UpdatePanel(_host.StateDirectory, _host.DrainForUpdateAsync, _host.QuitAsync);
        _history = new HistoryPanel(RunAsync);
        _thumbnails = new ThumbnailPanel(RunAsync);
        var quit = MakeButton("QuitButton", "Quit OrbitDrive");
        quit.Click += (_, _) => _ = _host.QuitAsync();
        _pause.Click += (_, _) => _ = _host.TogglePauseAsync();
        _pause.SetResourceReference(StyleProperty, "AccentButtonStyle");
        // Start is offered only while the agent is not running; pressing it is an explicit owner decision, so it
        // also clears the crash-restart budget.
        _start.Visibility = Visibility.Collapsed;
        _start.Click += (_, _) => { _host.Connection.StartExplicitly(); _ = _host.RefreshAsync(); };
        _stateText.Margin = new Thickness(0);
        var stats = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -14, 0) };
        stats.Children.Add(Stat(_statFolders, "Shared folders"));
        stats.Children.Add(Stat(_statTransfers, "Transfers running"));
        stats.Children.Add(Stat(_statDevices, "Paired devices"));
        Page("Overview",
            Card(null, Row(_stateDot, _stateText), _stateDetail, _meteredWarning, Row(_pause, _start), _summary),
            stats,
            _updates.Banner,
            Card("When you close this window",
                MakeText("CloseHint", "Closing this window keeps OrbitDrive running in the notification area so your phone can still reach shared folders. Quit stops remote access.", 13, FontWeights.Normal, Muted), quit));

        var browse = MakeButton("BrowseButton", "Browse…");
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to share (read-only)" };
            if (dialog.ShowDialog(_owner) == true) { _rootPath.Text = dialog.FolderName; if (_rootName.Text.Length == 0) _rootName.Text = Path.GetFileName(dialog.FolderName.TrimEnd('\\')); }
        };
        var add = MakeButton("AddRootButton", "Share this folder (read-only)", accent: true);
        add.Click += (_, _) => _ = AddRootAsync();
        var remove = MakeButton("RemoveRootButton", "Stop sharing selected folder");
        remove.Click += (_, _) => _ = RunAsync(_rootError, async client =>
        {
            if (_roots.SelectedIndex < 0 || _roots.SelectedIndex >= _rootIds.Count) throw new IpcException("Select a folder first.");
            await client.SendAsync("remove_root", new JsonObject { ["rootId"] = _rootIds[_roots.SelectedIndex] });
        });
        AutomationProperties.SetAutomationId(_newRootHidden, "NewRootHidden");
        var toggleHidden = MakeButton("ToggleHiddenButton", "Show or hide hidden items in selected folder");
        toggleHidden.Click += (_, _) => _ = RunAsync(_rootError, async client =>
        {
            var index = _roots.SelectedIndex;
            if (index < 0 || index >= _rootIds.Count) throw new IpcException("Select a folder first.");
            await client.SendAsync("set_root_hidden", new JsonObject { ["rootId"] = _rootIds[index], ["showHidden"] = !_rootHidden[index] });
        });
        var toggleUploads = MakeButton("ToggleUploadsButton", "Allow or stop uploads into selected folder");
        toggleUploads.Click += (_, _) => _ = RunAsync(_rootError, async client =>
        {
            var index = _roots.SelectedIndex;
            if (index < 0 || index >= _rootIds.Count) throw new IpcException("Select a folder first.");
            var turnOn = !_rootUploads[index];
            if (turnOn && MessageBox.Show(_owner, "Paired phones and browsers will be able to ADD files to this folder (up to 10 GiB in total, 4 GiB per file). They can never replace, delete or run anything. Turn uploads on?", "Allow uploads", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await client.SendAsync("set_root_uploads", new JsonObject { ["rootId"] = _rootIds[index], ["enabled"] = turnOn, ["quotaMiB"] = 10240, ["maxFileMiB"] = 4096 });
        });
        Page("Shared folders",
            MakeText("RootsIntro", "Only folders listed here can be seen from your paired devices. Sharing is read-only unless you allow uploads for a folder below, and Windows permissions still apply.", 13, FontWeights.Normal, Muted),
            Card("Shared now", _rootsEmpty, _roots, Row(remove, toggleHidden), Row(toggleUploads)),
            Card("Share another folder", MakeLabel("Name shown on your phone", _rootName), _rootName, MakeLabel("Folder", _rootPath), _rootPath, _newRootHidden, Row(browse, add), _rootError,
                MakeText("HiddenHint", "Hidden items stay private unless you tick the box. Windows system files are never shared. A folder is tied to the disk it is on: another disk at the same drive letter is not shared.", 13, FontWeights.Normal, Muted)));

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

        AutomationProperties.SetAutomationId(_transferList, "TransfersList");
        AutomationProperties.SetName(_transferList, "Recent transfers");
        ScrollViewer.SetHorizontalScrollBarVisibility(_transferList, ScrollBarVisibility.Disabled);
        Page("Transfers", Card(null, _transfers, _transferList,
            MakeText("TransfersHint", "OrbitDrive can see what it has sent to the relay. It cannot see whether a browser finished saving a file.", 13, FontWeights.Normal, Muted)));
        Page("Activity", _history.Card, Card("This session", _activity));

        var save = MakeButton("SaveGatewayButton", "Save and connect", accent: true);
        save.Click += (_, _) => _ = RunAsync(_settingsError, client =>
            client.SendAsync("set_gateway", new JsonObject { ["gateway"] = _gateway.Text.Trim(), ["webOrigin"] = _webOrigin.Text.Trim().Length == 0 ? null : _webOrigin.Text.Trim() }));
        _startAtSignIn.IsChecked = StartupRegistration.IsEnabled(_host.StateDirectory);
        // Checked/Unchecked (not Click) so that keyboard, mouse and assistive technology all take the same path.
        var applying = false;
        void ApplyStartAtSignIn(object sender, RoutedEventArgs e)
        {
            if (applying) return;
            applying = true;
            _settingsError.Text = "";
            try { StartupRegistration.Set(_host.StateDirectory, _startAtSignIn.IsChecked == true); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { _settingsError.Text = "Windows did not allow changing the start-up setting."; }
            _startAtSignIn.IsChecked = StartupRegistration.IsEnabled(_host.StateDirectory);   // show what is really registered
            applying = false;
        }
        _startAtSignIn.Checked += ApplyStartAtSignIn;
        _startAtSignIn.Unchecked += ApplyStartAtSignIn;
        _pauseWhenLocked.IsChecked = _host.Settings.PauseWhenLocked;
        void ApplyPauseWhenLocked(object sender, RoutedEventArgs e)
        {
            if (applying) return;
            applying = true;
            _settingsError.Text = "";
            try { _host.Settings.SetPauseWhenLocked(_pauseWhenLocked.IsChecked == true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _settingsError.Text = "The setting could not be saved."; }
            _host.Policies.LockEnabled = _host.Settings.PauseWhenLocked;
            _pauseWhenLocked.IsChecked = _host.Settings.PauseWhenLocked;
            applying = false;
        }
        _pauseWhenLocked.Checked += ApplyPauseWhenLocked;
        _pauseWhenLocked.Unchecked += ApplyPauseWhenLocked;
        _pauseOnMetered.IsChecked = _host.Settings.PauseOnMetered;
        void ApplyPauseOnMetered(object sender, RoutedEventArgs e)
        {
            if (applying) return;
            applying = true;
            _settingsError.Text = "";
            try { _host.Settings.SetPauseOnMetered(_pauseOnMetered.IsChecked == true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _settingsError.Text = "The setting could not be saved."; }
            _host.Policies.MeteredEnabled = _host.Settings.PauseOnMetered;
            _pauseOnMetered.IsChecked = _host.Settings.PauseOnMetered;
            applying = false;
        }
        _pauseOnMetered.Checked += ApplyPauseOnMetered;
        _pauseOnMetered.Unchecked += ApplyPauseOnMetered;
        var saveLimits = MakeButton("SaveLimitsButton", "Save limits");
        saveLimits.Click += (_, _) => _ = RunAsync(_limitsError, async client =>
        {
            if (!int.TryParse(_maxSpeed.Text.Trim(), out var speed) || !int.TryParse(_maxFiles.Text.Trim(), out var files))
                throw new IpcException("Enter whole numbers: a speed in KB/s (0 for no limit) and 1 to 16 files.");
            await client.SendAsync("set_limits", new JsonObject { ["maxUploadKBps"] = speed, ["maxConcurrentTransfers"] = files });
            _limitsShown = "";                                  // show what the agent actually stored
        });
        Page("Settings",
            Card("Connection", MakeLabel("Relay address (https://…)", _gateway), _gateway, MakeLabel("Web app address shown in pairing links (optional)", _webOrigin), _webOrigin, save),
            Card("Sending files", MakeLabel("Upload speed limit for all files together, in KB/s (0 = no limit)", _maxSpeed), _maxSpeed,
                MakeLabel("Files sent at the same time (1–16)", _maxFiles), _maxFiles, saveLimits, _limitsError,
                _pauseOnMetered,
                MakeText("MeteredHint", "Windows marks mobile hotspots and some other connections as metered. OrbitDrive shows a warning on such a connection; tick the box to stop sending until the connection is no longer metered.", 13, FontWeights.Normal, Muted)),
            Card("This PC", _startAtSignIn,
                MakeText("StartHint", "It starts quietly in the notification area, as you, without administrator rights. Off unless you turn it on.", 13, FontWeights.Normal, Muted),
                _pauseWhenLocked,
                MakeText("SleepNote", "While this PC is locked OrbitDrive keeps working unless you tick the box above. When the PC sleeps, shuts down or you sign out, your phone cannot reach it. OrbitDrive never changes your sleep settings or keeps the PC awake.", 13, FontWeights.Normal, Muted)),
            _thumbnails.Card,
            _updates.Card,
            _settingsError,
            MakeText("PrivacyNote", "Files travel over TLS through the relay, which can process them in transit and stores nothing. This is not end-to-end encryption.", 13, FontWeights.Normal, Muted));
        Page("Diagnostics", Card(null, _diagnostics));
    }

    private async Task RunAsync(TextBlock errorTarget, Func<LocalIpcClient, Task> action)
    {
        errorTarget.Text = "";
        var error = await _host.RunAsync(action);
        if (error is not null) errorTarget.Text = error;
    }


    private Task AddRootAsync() => RunAsync(_rootError, async client =>
    {
        var path = _rootPath.Text.Trim();
        if (path.Length > 0 && Path.GetPathRoot(path) == path &&
            MessageBox.Show(_owner, $"Share the whole drive {path}?\n\nEvery folder on it that your Windows account can read becomes visible to your paired devices.", "Share an entire drive", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        await client.SendAsync("add_root", new JsonObject { ["name"] = _rootName.Text.Trim(), ["path"] = path, ["showHidden"] = _newRootHidden.IsChecked == true });
        _rootName.Text = ""; _rootPath.Text = ""; _newRootHidden.IsChecked = false;
    });

    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} bytes" : $"{value:0.#} {units[unit]}";
    }

    private void SetDot(byte r, byte g, byte b) => _stateDot.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));

    private async Task ApplyStatusAsync(JsonObject? status)
    {
        if (status is null)
        {
            var connection = _host.Connection;
            (_stateText.Text, _stateDetail.Text) = connection.Phase switch
            {
                AgentPhase.KeepsStopping => ("Agent keeps stopping", $"OrbitDrive's background agent stopped unexpectedly {connection.MaxRestarts + 1} times in a short while, so it was not started again. Nothing is being shared."),
                AgentPhase.Stopped => ("Stopped", "The OrbitDrive agent was stopped. Nothing is being shared until you start it again."),
                AgentPhase.Starting => ("Starting…", "Starting OrbitDrive's background agent."),
                _ => ("Agent not running", "OrbitDrive's background agent is not running. Nothing is being shared."),
            };
            SetDot(0xC4, 0x2B, 0x1C);
            // Nothing is known while the agent is down: do not leave the last known numbers on screen.
            _summary.Text = "";
            _statFolders.Text = _statTransfers.Text = _statDevices.Text = "–";
            _pause.IsEnabled = false;
            _pause.Visibility = Visibility.Collapsed;
            _start.Visibility = _host.Connection.MayStart ? Visibility.Collapsed : Visibility.Visible;
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
        var shown = roots.ToJsonString();          // redraw when anything about a folder changes (availability, options), not only the set
        if (shown != _rootsShown)
        {
            var keep = _roots.SelectedIndex >= 0 && _roots.SelectedIndex < _rootIds.Count ? _rootIds[_roots.SelectedIndex] : null;
            _rootsShown = shown;
            _rootIds = ids;
            _rootHidden = [.. roots.Select(r => r!["showHidden"]?.GetValue<bool>() ?? false)];
            _rootUploads = [.. roots.Select(r => r!["uploadsEnabled"]?.GetValue<bool>() ?? false)];
            _roots.Items.Clear();
            foreach (var root in roots)
            {
                var available = (bool)root!["available"]!;
                var includesHidden = root["showHidden"]?.GetValue<bool>() ?? false;
                var title = new TextBlock { Text = root["name"]!.GetValue<string>(), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                var path = new TextBlock { Text = root["path"]!.GetValue<string>(), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                var uploadsOn = root["uploadsEnabled"]?.GetValue<bool>() ?? false;
                var badge = new TextBlock { Text = (available ? (uploadsOn ? "Accepts uploads from paired devices" : "Read-only") : "Read-only · currently unavailable (folder missing, or a different disk is at this location)") + (includesHidden ? " · hidden items included" : ""), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                Mute(path);
                if (available) Mute(badge); else badge.SetResourceReference(TextBlock.ForegroundProperty, Danger);
                var lines = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                lines.Children.Add(title); lines.Children.Add(path); lines.Children.Add(badge);
                var item = new ListBoxItem { Content = lines };
                AutomationProperties.SetName(item, $"{root["name"]}  —  {root["path"]}  ({(uploadsOn ? "accepts uploads" : "read-only")}{(available ? "" : ", currently unavailable")}{(includesHidden ? ", hidden items included" : "")})");
                _roots.Items.Add(item);
            }
            if (keep is not null) _roots.SelectedIndex = _rootIds.IndexOf(keep);
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
                var list = _host.Connection.Client is not { } deviceClient ? new JsonArray() : (await deviceClient.SendAsync("devices") as JsonArray) ?? new JsonArray();
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
        if (_host.Connection.Client is { } historyClient && CurrentPageName() == "Activity") await _history.RefreshAsync(historyClient);
        if (_host.Connection.Client is { } thumbnailClient && CurrentPageName() == "Settings") await _thumbnails.RefreshAsync(thumbnailClient);
        var approval = status["pendingApproval"];
        _approvalPanel.Visibility = approval is null ? Visibility.Collapsed : Visibility.Visible;
        _approvalText.Text = approval is null ? "" : $"Account: {approval["accountLabel"]}\nBrowser: {approval["browserName"]}\nCode: {approval["confirmationCode"]}\n\nApprove only if this is you and the same code is shown in your browser.";

        var limits = $"{status["maxUploadKBps"]}/{status["maxConcurrentTransfers"]}";
        if (limits != _limitsShown && !_maxSpeed.IsKeyboardFocused && !_maxFiles.IsKeyboardFocused)
        {
            _limitsShown = limits;
            _maxSpeed.Text = status["maxUploadKBps"]?.ToString() ?? "0";
            _maxFiles.Text = status["maxConcurrentTransfers"]?.ToString() ?? "4";
        }

        // Network cost: the host asks Windows now and then (a local query, no traffic); this only shows the result.
        var metered = _host.Policies.IsMetered;
        _meteredWarning.Text = metered ? $"This PC is on a metered connection ({OrbitDrive.Core.ConnectionCost.Describe(_host.Policies.CostFlags)}). Files sent to your devices count against its data allowance." : "";
        _meteredWarning.Visibility = metered ? Visibility.Visible : Visibility.Collapsed;

        var recent = status["transfers"] as JsonArray ?? [];
        var recentCount = recent.Count;
        _transfers.Text = (int)status["activeStreams"]! == 0 ? (recentCount == 0 ? "No transfers yet." : "No transfers are running. Recent ones are listed below.") : $"{status["activeStreams"]} file(s) are being sent to the relay right now.";
        var recentText = recent.ToJsonString();
        if (recentText != _transfersShown)
        {
            _transfersShown = recentText;
            _transferList.Items.Clear();
            foreach (var transfer in recent)
            {
                long sent = long.Parse(transfer!["sent"]!.GetValue<string>()), length = long.Parse(transfer["length"]!.GetValue<string>()), offset = long.Parse(transfer["offset"]!.GetValue<string>());
                var what = transfer["outcome"]!.GetValue<string>() switch
                {
                    "sending" => "being sent to the relay",
                    "sent_to_relay" => "sent to the relay",
                    "cancelled" => "stopped (cancelled, paused or access removed)",
                    _ => "interrupted",
                };
                var part = offset == 0 ? "" : $" (continued from {FormatBytes(offset)})";
                var line = $"{transfer["name"]}  —  {FormatBytes(sent)} of {FormatBytes(length)} {what}{part}  ·  {DateTimeOffset.FromUnixTimeMilliseconds(transfer["startedUnixMs"]!.GetValue<long>()).ToLocalTime():HH:mm:ss}";
                var item = new ListBoxItem { Content = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap } };
                AutomationProperties.SetName(item, line);
                _transferList.Items.Add(item);
            }
        }
        if (!_gateway.IsKeyboardFocused && _gateway.Text.Length == 0) _gateway.Text = status["gateway"]?.GetValue<string>() ?? "";
        _diagnostics.Text = $"PC id: {status["pcId"]}\nAgent version: {status["agentVersion"]}\nRelay: {status["gateway"] ?? "(not set)"}\nState folder: {_host.StateDirectory}\n" +
                            $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.FrameworkDescription}\nRunning elevated: {Environment.IsPrivilegedProcess}\n" +
                            $"Network cost: {OrbitDrive.Core.ConnectionCost.Describe(_host.Policies.CostFlags)}{(_host.Policies.CostFlags is { } raw ? $" (0x{raw:X})" : "")}\n" +
                            $"Theme: {Program.ThemeChoice}{(SystemParameters.HighContrast ? " (Windows high contrast is on)" : "")}\nAutomatic agent restarts used: {_host.Connection.RestartsUsed} of {_host.Connection.MaxRestarts} (per 10 minutes)";
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
}
