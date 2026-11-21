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
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    /// <summary>The shell tells this which page is on screen, so Activity and Settings are refreshed only while visible.</summary>
    public Func<string?> CurrentPageName { get; set; } = () => null;
    public IReadOnlyDictionary<string, FrameworkElement> Pages => _pages;

    private readonly CheckBox _startAtSignIn = new() { Content = "Start OrbitDrive when I sign in to Windows", Margin = new Thickness(0, 4, 0, 4) };
    private readonly CheckBox _pauseOnMetered = new() { Content = "Pause remote access while this PC is on a metered connection", Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _maxSpeed = MakeBox("MaxSpeedBox"), _maxFiles = MakeBox("MaxFilesBox");
    private readonly TextBlock _limitsError = MakeText("LimitsError", "", 13, FontWeights.Normal, Danger);
    private string _limitsShown = "";
    private readonly CheckBox _pauseWhenLocked = new() { Content = "Pause remote access while this PC is locked", Margin = new Thickness(0, 4, 0, 4) };

    private readonly TextBox _gateway = MakeBox("GatewayBox"), _webOrigin = MakeBox("WebOriginBox");
    private ThumbnailPanel _thumbnails = null!;
    private readonly TextBlock _settingsError = MakeText("SettingsError", "", 13, FontWeights.Normal, Danger);
    private readonly TextBlock _diagnostics = MakeText("DiagnosticsText", "", 13, FontWeights.Normal);
    private UpdatePanel _updates = null!;
    /// <summary>The software-update panel (still part of Settings); Home reads its state.</summary>
    public UpdatePanel Updates => _updates;


    public LegacyConsole(ConsoleHost host, Window owner)
    {
        _host = host;
        AutomationProperties.SetAutomationId(_startAtSignIn, "StartAtSignIn");
        AutomationProperties.SetAutomationId(_pauseWhenLocked, "PauseWhenLocked");
        AutomationProperties.SetAutomationId(_pauseOnMetered, "PauseOnMetered");
        BuildPages();
        host.AddStatusConsumer(ApplyStatusAsync);
    }


    internal static TextBlock MakeText(string id, string text, double size, FontWeight weight, string? brushKey = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        if (brushKey == Muted) Mute(block);
        else if (brushKey is not null) block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }

    internal static Button MakeButton(string id, string text, bool accent = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 34 };
        if (accent) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
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
        _thumbnails = new ThumbnailPanel(RunAsync);
        var quit = MakeButton("QuitButton", "Quit OrbitDrive");
        quit.Click += (_, _) => _ = _host.QuitAsync();



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
            Card("Quit OrbitDrive", MakeText("CloseHint", "Closing this window keeps OrbitDrive running in the notification area so your phone can still reach shared folders. Quit stops remote access.", 13, FontWeights.Normal, Muted), quit),
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




    private async Task ApplyStatusAsync(JsonObject? status)
    {
        if (status is null) return;   // the Home page says what is wrong; nothing here changes while the agent is down
        var state = status["state"]!.GetValue<string>();

        if (_host.Connection.Client is { } thumbnailClient && CurrentPageName() == "Settings") await _thumbnails.RefreshAsync(thumbnailClient);

        var limits = $"{status["maxUploadKBps"]}/{status["maxConcurrentTransfers"]}";
        if (limits != _limitsShown && !_maxSpeed.IsKeyboardFocused && !_maxFiles.IsKeyboardFocused)
        {
            _limitsShown = limits;
            _maxSpeed.Text = status["maxUploadKBps"]?.ToString() ?? "0";
            _maxFiles.Text = status["maxConcurrentTransfers"]?.ToString() ?? "4";
        }

        if (!_gateway.IsKeyboardFocused && _gateway.Text.Length == 0) _gateway.Text = status["gateway"]?.GetValue<string>() ?? "";
        _diagnostics.Text = $"PC id: {status["pcId"]}\nAgent version: {status["agentVersion"]}\nRelay: {status["gateway"] ?? "(not set)"}\nState folder: {_host.StateDirectory}\n" +
                            $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.FrameworkDescription}\nRunning elevated: {Environment.IsPrivilegedProcess}\n" +
                            $"Network cost: {OrbitDrive.Core.ConnectionCost.Describe(_host.Policies.CostFlags)}{(_host.Policies.CostFlags is { } raw ? $" (0x{raw:X})" : "")}\n" +
                            $"Theme: {Program.ThemeChoice}{(SystemParameters.HighContrast ? " (Windows high contrast is on)" : "")}\nAutomatic agent restarts used: {_host.Connection.RestartsUsed} of {_host.Connection.MaxRestarts} (per 10 minutes)";
    }

}
