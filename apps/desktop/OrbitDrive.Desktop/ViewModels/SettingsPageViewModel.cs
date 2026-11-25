using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using OrbitDrive.Core;

namespace OrbitDrive.Desktop.ViewModels;

/// <summary>
/// Settings: General, Sending (limits, metered connections, picture previews, Explorer "Send to"), Privacy (plain words about what the relay can see) and
/// Advanced (the read-only relay address, software updates, diagnostics, Quit). Every value shown is what the agent or Windows actually reports or stored.
/// </summary>
internal sealed class SettingsPageViewModel : PageViewModel
{
    private readonly ConsoleHost _host;
    private readonly Action<string> _copy;
    private bool _start, _lock, _metered, _thumbsEnabled, _thumbsAvailable, _canClearThumbs, _applyingThumbs, _limitsDirty, _pcNameDirty;
    private string _pcName = "", _pcNameError = "";
    private string _maxSpeed = "0", _maxFiles = "4", _limitsShown = "", _gateway = "", _webOrigin = "";
    private string _settingsError = "", _limitsError = "", _thumbsStatus = "", _thumbsError = "", _sendToStatus = "", _sendToError = "", _diagnostics = "";
    private string _thumbsShown = "";

    public SettingsPageViewModel(ConsoleHost host, UpdatePanel updates, Action<string> copy)
    {
        _host = host; _copy = copy; Updates = updates;
        _start = StartupRegistration.IsEnabled(host.StateDirectory);
        _lock = host.Settings.PauseWhenLocked;
        _metered = host.Settings.PauseOnMetered;
        SaveLimitsCommand = new AsyncRelayCommand(SaveLimitsAsync);
        SaveGatewayCommand = new AsyncRelayCommand(SaveGatewayAsync);
        SavePcNameCommand = new AsyncRelayCommand(SavePcNameAsync);
        ClearThumbnailsCommand = new AsyncRelayCommand(ClearThumbnailsAsync);
        SendToOnCommand = new AsyncRelayCommand(() => SetSendToAsync(true));
        SendToOffCommand = new AsyncRelayCommand(() => SetSendToAsync(false));
        CopyDiagnosticsCommand = new RelayCommand(() => { try { _copy(_diagnostics); } catch (ExternalException) { SettingsError = "Windows would not let MyOrbitDrive use the clipboard just now."; } });
        QuitCommand = new AsyncRelayCommand(host.QuitAsync);
        host.AddStatusConsumer(OnStatus);
    }

    public override string Title => PageNames.Settings;

    public IRelayCommand SaveLimitsCommand { get; }
    public IRelayCommand SaveGatewayCommand { get; }
    public IRelayCommand SavePcNameCommand { get; }
    public IRelayCommand ClearThumbnailsCommand { get; }
    public IRelayCommand SendToOnCommand { get; }
    public IRelayCommand SendToOffCommand { get; }
    public IRelayCommand CopyDiagnosticsCommand { get; }
    public IRelayCommand QuitCommand { get; }

    /// <summary>The software-update panel is still code-built (see the design doc); it is hosted as is.</summary>
    public UpdatePanel Updates { get; }

    // ---- General -----------------------------------------------------------------------------------------------------------------------------
    public bool StartAtSignIn
    {
        get => _start;
        set
        {
            SettingsError = "";
            try { StartupRegistration.Set(_host.StateDirectory, value); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { SettingsError = "Windows did not allow changing the start-up setting."; }
            _start = StartupRegistration.IsEnabled(_host.StateDirectory);   // show what is really registered
            OnPropertyChanged();
        }
    }

    public bool PauseWhenLocked
    {
        get => _lock;
        set
        {
            SettingsError = "";
            try { _host.Settings.SetPauseWhenLocked(value); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SettingsError = "The setting could not be saved."; }
            _host.Policies.LockEnabled = _host.Settings.PauseWhenLocked;
            _lock = _host.Settings.PauseWhenLocked;
            OnPropertyChanged();
        }
    }

    // ---- Sending -----------------------------------------------------------------------------------------------------------------------------
    public bool PauseOnMetered
    {
        get => _metered;
        set
        {
            SettingsError = "";
            try { _host.Settings.SetPauseOnMetered(value); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SettingsError = "The setting could not be saved."; }
            _host.Policies.MeteredEnabled = _host.Settings.PauseOnMetered;
            _metered = _host.Settings.PauseOnMetered;
            OnPropertyChanged();
        }
    }

    public string MaxSpeed { get => _maxSpeed; set { if (SetProperty(ref _maxSpeed, value)) _limitsDirty = true; } }
    public string MaxFiles { get => _maxFiles; set { if (SetProperty(ref _maxFiles, value)) _limitsDirty = true; } }
    public string LimitsError { get => _limitsError; private set => SetProperty(ref _limitsError, value); }

    public bool ThumbnailsEnabled
    {
        get => _thumbsEnabled;
        set
        {
            if (_applyingThumbs || !SetProperty(ref _thumbsEnabled, value)) return;
            _thumbsShown = "";   // always redraw from what the agent says it stored
            _ = ApplyThumbnailsAsync(client => client.SendAsync("set_thumbnails", new JsonObject { ["enabled"] = value }));
        }
    }
    public bool ThumbnailsAvailable { get => _thumbsAvailable; private set => SetProperty(ref _thumbsAvailable, value); }
    public bool CanClearThumbnails { get => _canClearThumbs; private set => SetProperty(ref _canClearThumbs, value); }
    public string ThumbnailStatus { get => _thumbsStatus; private set => SetProperty(ref _thumbsStatus, value); }
    public string ThumbnailError { get => _thumbsError; private set => SetProperty(ref _thumbsError, value); }

    public string SendToStatus { get => _sendToStatus; private set => SetProperty(ref _sendToStatus, value); }
    public string SendToError { get => _sendToError; private set => SetProperty(ref _sendToError, value); }

    // ---- Advanced ----------------------------------------------------------------------------------------------------------------------------
    /// <summary>The name paired devices see for this PC. Empty goes back to the Windows computer name.</summary>
    public string PcName { get => _pcName; set { if (SetProperty(ref _pcName, value)) _pcNameDirty = true; } }
    public string PcNameError { get => _pcNameError; private set => SetProperty(ref _pcNameError, value); }
    /// <summary>The relay address the agent reports. Shown read-only under Diagnostics; it is no longer edited here, and saving the web app address sends it back unchanged.</summary>
    public string Gateway { get => _gateway; private set => SetProperty(ref _gateway, value); }
    public string WebOrigin { get => _webOrigin; set => SetProperty(ref _webOrigin, value); }
    public string SettingsError { get => _settingsError; private set => SetProperty(ref _settingsError, value); }
    public string Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }

    public string PrivacyHeadline => "Your files are not stored on a server.";
    public string PrivacyBody =>
        "When a paired device opens or downloads a file, it passes through MyOrbitDrive's relay server on its way to that device. The connection is encrypted in transit (HTTPS), " +
        "but MyOrbitDrive is not end-to-end encrypted: while a file is passing through, the relay can see its contents and its name. The relay keeps no copy afterwards.";
    public string PrivacyOnThisPc =>
        "Kept on this PC only: your settings, the history on the Activity page (no file names) and the small picture-preview cache. Only folders you share can be seen, " +
        "and paired devices can look and download. They can add files only to folders where you allowed it.";

    // ---- Behaviour ---------------------------------------------------------------------------------------------------------------------------
    private async Task SaveLimitsAsync()
    {
        LimitsError = "";
        if (!int.TryParse(MaxSpeed.Trim(), out var speed) || !int.TryParse(MaxFiles.Trim(), out var files))
        {
            LimitsError = "Enter whole numbers: a speed in KB/s (0 for no limit) and 1 to 16 files.";
            return;
        }
        LimitsError = await _host.RunAsync(async client =>
        {
            await client.SendAsync("set_limits", new JsonObject { ["maxUploadKBps"] = speed, ["maxConcurrentTransfers"] = files });
            _limitsShown = ""; _limitsDirty = false;   // show what the agent actually stored
        }) ?? "";
    }

    private async Task SavePcNameAsync()
    {
        PcNameError = await _host.RunAsync(async client =>
        {
            await client.SendAsync("set_pc_name", new JsonObject { ["name"] = PcName.Trim() });
            _pcNameDirty = false;
        }) ?? "";
    }

    private async Task SaveGatewayAsync()
    {
        SettingsError = "";
        var web = WebOrigin.Trim();
        SettingsError = await _host.RunAsync(async client =>
        {
            await client.SendAsync("set_gateway", new JsonObject { ["gateway"] = Gateway.Trim(), ["webOrigin"] = web.Length == 0 ? null : web });
        }) ?? "";
    }

    private async Task ApplyThumbnailsAsync(Func<OrbitDrive.Core.Ipc.LocalIpcClient, Task<JsonNode?>> call)
    {
        ThumbnailError = "";
        JsonNode? reply = null;
        ThumbnailError = await _host.RunAsync(async client => reply = await call(client)) ?? "";
        ApplyThumbnails(reply as JsonObject);
    }

    private Task ClearThumbnailsAsync() => ApplyThumbnailsAsync(client => client.SendAsync("clear_thumbnails"));

    private void ApplyThumbnails(JsonObject? status)
    {
        if (status is null) return;
        var enabled = status["enabled"]?.GetValue<bool>() ?? false;
        // "available" in the agent means enabled AND a helper exists, so it is false while the owner has switched previews off. The switch must stay usable
        // then, so what matters here is only whether the helper that makes previews exists on this PC.
        var helper = status["hasHelper"]?.GetValue<bool>() ?? false;
        var files = status["files"]?.GetValue<long>() ?? 0;
        var bytes = status["bytes"]?.GetValue<long>() ?? 0;
        var signature = $"{enabled}|{helper}|{files}|{bytes}";
        if (signature == _thumbsShown) return;
        _thumbsShown = signature;
        _applyingThumbs = true;
        try
        {
            _thumbsEnabled = enabled; OnPropertyChanged(nameof(ThumbnailsEnabled));
            ThumbnailsAvailable = helper;
            CanClearThumbnails = helper && files > 0;
            ThumbnailStatus = ThumbnailSummary.Describe(enabled, helper, files, bytes);
        }
        finally { _applyingThumbs = false; }
    }

    private async Task SetSendToAsync(bool on)
    {
        SendToError = "";
        JsonNode? reply = null;
        SendToError = await _host.RunAsync(async client => reply = await client.SendAsync("set_sendto", new JsonObject { ["enabled"] = on })) ?? "";
        if (SendToError.Length == 0)
            SendToStatus = (reply as JsonObject)?["enabled"]?.GetValue<bool>() == true
                ? "Turned on. Your paired devices appear in Explorer's Send to menu."
                : "Turned off. The Send to shortcuts were removed.";
    }

    private async Task OnStatus(ConsoleStatus? status)
    {
        if (status is null) return;
        if (IsActive && _host.Connection.Client is { } client)
        {
            try { ApplyThumbnails(await client.SendAsync("thumbnails") as JsonObject); }
            catch (Exception e) when (e is OrbitDrive.Core.Ipc.IpcException or TimeoutException) { }
        }
        var limits = $"{status.MaxUploadKBps}/{status.MaxConcurrentTransfers}";
        if (limits != _limitsShown && !_limitsDirty)
        {
            _limitsShown = limits;
            SetProperty(ref _maxSpeed, status.MaxUploadKBps.ToString(), nameof(MaxSpeed));
            SetProperty(ref _maxFiles, status.MaxConcurrentTransfers.ToString(), nameof(MaxFiles));
        }
        if (!_pcNameDirty) SetProperty(ref _pcName, status.PcName, nameof(PcName));
        if (_gateway != (status.Gateway ?? "")) Gateway = status.Gateway ?? "";
        Diagnostics = $"PC id: {status.PcId}\nAgent version: {status.AgentVersion}\nRelay: {status.Gateway ?? "(not set)"}\nState folder: {_host.StateDirectory}\n" +
                      $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · {RuntimeInformation.FrameworkDescription}\nRunning elevated: {Environment.IsPrivilegedProcess}\n" +
                      $"Network cost: {ConnectionCost.Describe(_host.Policies.CostFlags)}{(_host.Policies.CostFlags is { } raw ? $" (0x{raw:X})" : "")}\n" +
                      $"Theme: {Program.ThemeChoice}{(SystemParameters.HighContrast ? " (Windows high contrast is on)" : "")}\nAutomatic agent restarts used: {_host.Connection.RestartsUsed} of {_host.Connection.MaxRestarts} (per 10 minutes)";
    }
}
