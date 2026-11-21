using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Threading;
using OrbitDrive.Core;
using OrbitDrive.Core.Ipc;
using OrbitDrive.Protocol;

namespace OrbitDrive.Desktop;

/// <summary>
/// "Software updates" in Settings: check, then update in place with one button. The update is a signed manifest from the project's release feed
/// (verified by <see cref="UpdateVerifier"/>), a zip checked for size and SHA-256 while it downloads, applied by the embedded script after sharing is
/// paused and the app has quit. Nothing is ever installed without the owner pressing the button; the check itself only reads a small public file.
/// </summary>
internal sealed class UpdatePanel : ObservableObject
{
    public static readonly Uri Feed = new("https://github.com/nisar87/OrbitDrive/releases/download/desktop-dev/update.json");
    private const string Channel = "dev";

    private readonly UpdateClient _client;
    private readonly UpdateStager _stager;
    private readonly string _resultFile, _installDir, _stateDirectory;
    private readonly Func<CancellationToken, Task> _drain;
    private readonly Func<Task> _quitForUpdate;
    private readonly Version _running = UpdateTrust.RunningVersion();
    private string _versionText = "", _status = "", _notes = "", _banner = "";
    private bool _canCheck = true, _canInstall = true, _showInstall, _showBanner;
    private VerifiedUpdate? _available;
    private bool _busy;

    /// <summary>The version an update is waiting for, or null. Home shows a notice from this.</summary>
    public string? AvailableVersion { get; private set; }
    public event Action? Changed;

    public string VersionText { get => _versionText; private set => SetProperty(ref _versionText, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Notes { get => _notes; private set => SetProperty(ref _notes, value); }
    public string Banner { get => _banner; private set => SetProperty(ref _banner, value); }
    public bool CanCheck { get => _canCheck; private set => SetProperty(ref _canCheck, value); }
    public bool CanInstall { get => _canInstall; private set => SetProperty(ref _canInstall, value); }
    public bool ShowInstall { get => _showInstall; private set => SetProperty(ref _showInstall, value); }
    public bool ShowBanner { get => _showBanner; private set => SetProperty(ref _showBanner, value); }
    public string Hint => "Updating replaces the program files in place and restarts OrbitDrive. Your shared folders, paired devices and settings are kept. Nothing is installed until you press Update now.";
    public IRelayCommand CheckCommand { get; }
    public IRelayCommand InstallCommand { get; }

    public UpdatePanel(string stateDirectory, Func<CancellationToken, Task> drainActiveWork, Func<Task> quitForUpdate)
    {
        _stateDirectory = stateDirectory;
        _drain = drainActiveWork; _quitForUpdate = quitForUpdate;
        var folder = Path.Join(stateDirectory, "updates");
        _stager = new UpdateStager(folder);
        _resultFile = Path.Join(folder, "apply-result.json");
        _installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var keys = UpdateTrust.Load();
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OrbitDrive-Updater/" + _running);
        var verifier = new UpdateVerifier(keys, ["github.com"], Channel, "x64", ProtocolInfo.Version, TimeProvider.System);
        _client = new UpdateClient(http, verifier, _stager, Feed, keys.Count > 0);

        VersionText = $"OrbitDrive {_running}";
        CheckCommand = new RelayCommand(() => _ = CheckAsync(silent: false));
        InstallCommand = new RelayCommand(() => _ = InstallAsync());

        ReportLastUpdate();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        timer.Tick += (_, _) => _ = CheckAsync(silent: true);
        timer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => Application.Current?.Dispatcher.InvokeAsync(() => CheckAsync(silent: true)), TaskScheduler.Default);
    }

    /// <summary>After a restart: tell the owner whether the last update worked, once.</summary>
    private void ReportLastUpdate()
    {
        var outcome = _stager.Recover(_running);
        var result = PortableUpdateLauncher.ReadResult(_resultFile);
        if (result is not null)
        {
            try { File.Delete(_resultFile); } catch (IOException) { }
            Status = result.Ok ? $"Updated. {result.Message}" : $"The last update did not finish and the previous version is still in use. {result.Message}";
        }
        else if (outcome == UpdateStager.RecoveryOutcome.ApplyInterrupted) Status = "The last update was interrupted. This version still works; you can try again.";
    }

    public async Task CheckAsync(bool silent)
    {
        if (_busy) return;
        _busy = true; CanCheck = false;
        if (!silent) Status = "Checking…";
        try
        {
            var check = await _client.CheckAsync(_running, CancellationToken.None);
            _available = check.Update;
            AvailableVersion = check.State == UpdateState.Available ? check.Update!.Version.ToString() : null;
            Changed?.Invoke();
            ShowInstall = ShowBanner = check.State == UpdateState.Available;
            Banner = check.State == UpdateState.Available ? $"Update available: {check.Update!.Version}. Open Settings and press Update now." : "";
            Notes = check.State == UpdateState.Available ? check.Update!.Notes : "";
            if (!silent || check.State is UpdateState.Available or UpdateState.Rejected) Status = check.Message;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { if (!silent) Status = "Could not check for updates."; }
        finally { _busy = false; CanCheck = true; }
    }

    private async Task InstallAsync()
    {
        if (_busy || _available is not { } update) return;
        var ok = MessageBox.Show($"Update OrbitDrive to {update.Version}?\n\nSharing is paused, files being sent are stopped, OrbitDrive restarts by itself, and your folders, paired devices and settings are kept.",
            "Update OrbitDrive", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        _busy = true; CanCheck = false; CanInstall = false;
        try
        {
            Status = "Downloading and checking the update…";
            await _client.DownloadAsync(update, CancellationToken.None);
            Status = "Pausing sharing and updating…";
            var launcher = new PortableUpdateLauncher(_installDir, _stateDirectory, update.File.Sha256, update.Version, _resultFile);
            await _stager.ApplyAsync(_drain, launcher, TimeSpan.FromSeconds(60), CancellationToken.None);
            Status = "Restarting OrbitDrive…";
            await _quitForUpdate();              // the update script waits for every OrbitDrive program to exit, then swaps the folder and starts the new version
        }
        catch (UpdateRejectedException e) { Status = e.Message; }
        catch (Exception e) when (e is IOException or InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception) { Status = "The update could not be started: " + e.Message; }
        finally { _busy = false; CanCheck = true; CanInstall = true; }
    }
}
