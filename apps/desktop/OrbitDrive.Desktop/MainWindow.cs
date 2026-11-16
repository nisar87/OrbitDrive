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
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x5D, 0x5D, 0x5D));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));

    private readonly string _stateDirectory;
    private readonly ListBox _nav = new() { Width = 190, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 14 };
    private readonly Grid _content = new();
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Windows.Forms.NotifyIcon _tray = new();
    private LocalIpcClient? _client;
    private bool _quitting, _connecting, _paused, _autoStartUsed;

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
        Title = "OrbitDrive";
        Width = 900; Height = 600; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
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
        foreach (var page in Pages) _nav.Items.Add(page);
        _nav.SelectionChanged += (_, _) => { foreach (var (name, element) in _pages) element.Visibility = name == (string?)_nav.SelectedItem ? Visibility.Visible : Visibility.Collapsed; };
        _nav.SelectedIndex = 0;

        var layout = new DockPanel();
        var side = new Border { Background = new SolidColorBrush(Color.FromRgb(0xE9, 0xEE, 0xF6)), Padding = new Thickness(8, 16, 8, 16), Child = _nav };
        DockPanel.SetDock(side, Dock.Left);
        layout.Children.Add(side);
        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(28, 20, 28, 20), Content = _content });
        Content = layout;

        _tray.Icon = System.Drawing.SystemIcons.Application;
        _tray.Text = "OrbitDrive";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => Reopen();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open OrbitDrive", null, (_, _) => Reopen());
        menu.Items.Add("Pause / resume remote access", null, (_, _) => _ = TogglePauseAsync());
        menu.Items.Add("Quit OrbitDrive (stops remote access)", null, (_, _) => _ = QuitAsync());
        _tray.ContextMenuStrip = menu;

        _timer.Tick += (_, _) => _ = RefreshAsync();
        Loaded += (_, _) => { _timer.Start(); _ = RefreshAsync(); };
    }

    private static TextBlock MakeText(string id, string text, double size, FontWeight weight, Brush? brush = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        if (brush is not null) block.Foreground = brush;
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    private static Button MakeButton(string id, string text)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 34 };
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    private static TextBox MakeBox(string id)
    {
        var box = new TextBox { Padding = new Thickness(6), Margin = new Thickness(0, 2, 0, 8), MinHeight = 32 };
        AutomationProperties.SetAutomationId(box, id);
        return box;
    }

    private static TextBlock MakeLabel(string text, TextBox target)
    {
        var label = new TextBlock { Text = text, FontSize = 13, Foreground = Muted };
        AutomationProperties.SetName(target, text);
        return label;
    }

    private StackPanel Page(string name, params UIElement[] children)
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };
        panel.Children.Add(new TextBlock { Text = name, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
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
        var start = MakeButton("StartAgentButton", "Start the OrbitDrive agent");
        start.Click += (_, _) => { _autoStartUsed = false; _ = RefreshAsync(); };
        Page("Overview", _stateText, _stateDetail, _pause, start, _summary,
            MakeText("CloseHint", "Closing this window keeps OrbitDrive running in the notification area so your phone can still reach shared folders. Quit stops remote access.", 13, FontWeights.Normal, Muted), quit);

        var browse = MakeButton("BrowseButton", "Browse…");
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to share (read-only)" };
            if (dialog.ShowDialog(this) == true) { _rootPath.Text = dialog.FolderName; if (_rootName.Text.Length == 0) _rootName.Text = Path.GetFileName(dialog.FolderName.TrimEnd('\\')); }
        };
        var add = MakeButton("AddRootButton", "Share this folder (read-only)");
        add.Click += (_, _) => _ = AddRootAsync();
        var remove = MakeButton("RemoveRootButton", "Stop sharing selected folder");
        remove.Click += (_, _) => _ = RunAsync(_rootError, async client =>
        {
            if (_roots.SelectedIndex < 0 || _roots.SelectedIndex >= _rootIds.Count) throw new IpcException("Select a folder first.");
            await client.SendAsync("remove_root", new JsonObject { ["rootId"] = _rootIds[_roots.SelectedIndex] });
        });
        Page("Shared folders",
            MakeText("RootsIntro", "Only folders listed here can be seen from your paired devices. Sharing is read-only, and Windows permissions still apply.", 13, FontWeights.Normal, Muted),
            _rootsEmpty, _roots, remove, MakeLabel("Name shown on your phone", _rootName), _rootName, MakeLabel("Folder", _rootPath), _rootPath, browse, add, _rootError);

        var pair = MakeButton("PairButton", "Pair a phone or browser");
        pair.Click += (_, _) => _ = RunAsync(_deviceError, client => client.SendAsync("pair"));
        var approve = MakeButton("ApproveButton", "Approve");
        approve.Click += (_, _) => _ = RunAsync(_deviceError, client => client.SendAsync("approve"));
        var deny = MakeButton("DenyButton", "Deny");
        deny.Click += (_, _) => _ = RunAsync(_deviceError, client => client.SendAsync("deny"));
        var decision = new StackPanel { Orientation = Orientation.Horizontal };
        decision.Children.Add(approve);
        decision.Children.Add(deny);
        _approvalPanel.Children.Add(new TextBlock { Text = "A browser wants access", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        _approvalPanel.Children.Add(_approvalText);
        _approvalPanel.Children.Add(decision);
        var revoke = MakeButton("RevokeDeviceButton", "Remove selected device's access");
        revoke.Click += (_, _) => _ = RunAsync(_deviceError, async client =>
        {
            if (_devices.SelectedIndex < 0 || _devices.SelectedIndex >= _deviceIds.Count) throw new IpcException("Select a device first.");
            await client.SendAsync("revoke_device", new JsonObject { ["deviceId"] = _deviceIds[_devices.SelectedIndex] });
            _tick = 0;
        });
        Page("Devices", pair, _pairQr, _pairCode, _pairLink, _pairHelp, _approvalPanel, _deviceError,
            new TextBlock { Text = "Paired devices", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 4) }, _devicesEmpty, _devices, revoke);

        Page("Transfers", _transfers,
            MakeText("TransfersHint", "OrbitDrive can see what it has sent to the relay. It cannot see whether a browser finished saving a file.", 13, FontWeights.Normal, Muted));
        Page("Activity", _activity);

        var save = MakeButton("SaveGatewayButton", "Save and connect");
        save.Click += (_, _) => _ = RunAsync(_settingsError, client =>
            client.SendAsync("set_gateway", new JsonObject { ["gateway"] = _gateway.Text.Trim(), ["webOrigin"] = _webOrigin.Text.Trim().Length == 0 ? null : _webOrigin.Text.Trim() }));
        Page("Settings", MakeLabel("Relay address (https://…)", _gateway), _gateway, MakeLabel("Web app address shown in pairing links (optional)", _webOrigin), _webOrigin, save, _settingsError,
            MakeText("PrivacyNote", "Files travel over TLS through the relay, which can process them in transit and stores nothing. This is not end-to-end encryption.", 13, FontWeights.Normal, Muted));
        Page("Diagnostics", _diagnostics);
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
                    client.Disconnected += () => Dispatcher.InvokeAsync(() => { _client = null; });
                    return _client = client;
                }
                catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    // Start the agent automatically once per console session. If it stops later (for example the
                    // owner quit it elsewhere) it is not relaunched in a loop; the owner restarts it explicitly.
                    if (attempt != 0 || _autoStartUsed || !Program.StartAgent(_stateDirectory)) break;
                    _autoStartUsed = true;
                }
            }
            return null;
        }
        finally { _connecting = false; }
    }

    private void OnEvent(string name, JsonNode? data)
    {
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
            _ => name,
        };
        _activity.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (_activity.Items.Count > 200) _activity.Items.RemoveAt(_activity.Items.Count - 1); // bounded history
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

    private Task TogglePauseAsync() => RunAsync(_stateDetail, client => client.SendAsync(_paused ? "resume" : "pause"));

    private async Task RefreshAsync()
    {
        if (_quitting) return;
        JsonObject? status = null;
        try { if (await ConnectAsync() is { } client) status = await client.SendAsync("status") as JsonObject; }
        catch (Exception e) when (e is IpcException or TimeoutException) { }
        if (status is null)
        {
            _stateText.Text = "Agent not running";
            _stateDetail.Text = "OrbitDrive's background agent could not be started. Nothing is being shared.";
            _pause.IsEnabled = false;
            return;
        }
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
        _pause.IsEnabled = true;
        _pause.Content = _paused ? "Resume remote access" : "Pause remote access";

        var roots = status["roots"]!.AsArray();
        _summary.Text = $"{roots.Count} shared folder{(roots.Count == 1 ? "" : "s")} · {status["activeStreams"]} active transfer{((int)status["activeStreams"]! == 1 ? "" : "s")} · PC name: {status["pcName"]}";
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
            }
            catch (Exception e) when (e is IpcException or TimeoutException) { }
        }
        var approval = status["pendingApproval"];
        _approvalPanel.Visibility = approval is null ? Visibility.Collapsed : Visibility.Visible;
        _approvalText.Text = approval is null ? "" : $"Account: {approval["accountLabel"]}\nBrowser: {approval["browserName"]}\nCode: {approval["confirmationCode"]}\n\nApprove only if this is you and the same code is shown in your browser.";

        _transfers.Text = (int)status["activeStreams"]! == 0 ? "No transfers are running." : $"{status["activeStreams"]} file(s) are being sent to the relay right now.";
        if (!_gateway.IsKeyboardFocused && _gateway.Text.Length == 0) _gateway.Text = status["gateway"]?.GetValue<string>() ?? "";
        _diagnostics.Text = $"PC id: {status["pcId"]}\nAgent version: {status["agentVersion"]}\nRelay: {status["gateway"] ?? "(not set)"}\nState folder: {_stateDirectory}\n" +
                            $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.FrameworkDescription}\nRunning elevated: {Environment.IsPrivilegedProcess}";
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

    private void Reopen() { Show(); WindowState = WindowState.Normal; Activate(); }

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
        try { if (_client is not null) await _client.SendAsync("quit"); } catch (Exception e) when (e is IpcException or TimeoutException) { }
        _tray.Visible = false;
        _tray.Dispose();
        Application.Current.Shutdown();
    }
}
