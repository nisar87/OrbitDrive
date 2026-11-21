using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
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
internal sealed class UpdatePanel
{
    public static readonly Uri Feed = new("https://github.com/nisar87/OrbitDrive/releases/download/desktop-dev/update.json");
    private const string Channel = "dev";

    private readonly UpdateClient _client;
    private readonly UpdateStager _stager;
    private readonly string _resultFile, _installDir, _stateDirectory;
    private readonly Func<CancellationToken, Task> _drain;
    private readonly Func<Task> _quitForUpdate;
    private readonly Version _running = UpdateTrust.RunningVersion();
    private readonly TextBlock _version = LegacyConsole.MakeText("UpdateVersion", "", 14, FontWeights.SemiBold);
    private readonly TextBlock _status = LegacyConsole.MakeText("UpdateStatus", "", 13, FontWeights.Normal);
    private readonly TextBlock _notes = LegacyConsole.MakeText("UpdateNotes", "", 13, FontWeights.Normal, LegacyConsole.Muted);
    private readonly Button _check = LegacyConsole.MakeButton("CheckUpdateButton", "Check for updates");
    private readonly Button _install = LegacyConsole.MakeButton("InstallUpdateButton", "Update now", accent: true);
    private readonly TextBlock _banner = LegacyConsole.MakeText("UpdateBanner", "", 13, FontWeights.SemiBold);
    private VerifiedUpdate? _available;
    private bool _busy;

    public Border Card { get; }
    /// <summary>The version an update is waiting for, or null. Home shows a notice from this.</summary>
    public string? AvailableVersion { get; private set; }
    public event Action? Changed;
    /// <summary>A one-line notice for the Overview page; collapsed unless an update is waiting.</summary>
    public TextBlock Banner => _banner;

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

        _version.Text = $"OrbitDrive {_running}";
        _install.Visibility = Visibility.Collapsed;
        _banner.Visibility = Visibility.Collapsed;
        _check.Click += (_, _) => _ = CheckAsync(silent: false);
        _install.Click += (_, _) => _ = InstallAsync();
        Card = LegacyConsole.Card("Software updates", _version, _status, _notes, LegacyConsole.Row(_check, _install),
            LegacyConsole.MakeText("UpdateHint", "Updating replaces the program files in place and restarts OrbitDrive. Your shared folders, paired devices and settings are kept. Nothing is installed until you press Update now.", 13, FontWeights.Normal, LegacyConsole.Muted));

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
            _status.Text = result.Ok ? $"Updated. {result.Message}" : $"The last update did not finish and the previous version is still in use. {result.Message}";
        }
        else if (outcome == UpdateStager.RecoveryOutcome.ApplyInterrupted) _status.Text = "The last update was interrupted. This version still works; you can try again.";
    }

    public async Task CheckAsync(bool silent)
    {
        if (_busy) return;
        _busy = true; _check.IsEnabled = false;
        if (!silent) _status.Text = "Checking…";
        try
        {
            var check = await _client.CheckAsync(_running, CancellationToken.None);
            _available = check.Update;
            AvailableVersion = check.State == UpdateState.Available ? check.Update!.Version.ToString() : null;
            Changed?.Invoke();
            _install.Visibility = check.State == UpdateState.Available ? Visibility.Visible : Visibility.Collapsed;
            _banner.Visibility = check.State == UpdateState.Available ? Visibility.Visible : Visibility.Collapsed;
            _banner.Text = check.State == UpdateState.Available ? $"Update available: {check.Update!.Version}. Open Settings and press Update now." : "";
            _notes.Text = check.State == UpdateState.Available ? check.Update!.Notes : "";
            if (!silent || check.State is UpdateState.Available or UpdateState.Rejected) _status.Text = check.Message;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { if (!silent) _status.Text = "Could not check for updates."; }
        finally { _busy = false; _check.IsEnabled = true; }
    }

    private async Task InstallAsync()
    {
        if (_busy || _available is not { } update) return;
        var ok = MessageBox.Show($"Update OrbitDrive to {update.Version}?\n\nSharing is paused, files being sent are stopped, OrbitDrive restarts by itself, and your folders, paired devices and settings are kept.",
            "Update OrbitDrive", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        _busy = true; _check.IsEnabled = false; _install.IsEnabled = false;
        try
        {
            _status.Text = "Downloading and checking the update…";
            await _client.DownloadAsync(update, CancellationToken.None);
            _status.Text = "Pausing sharing and updating…";
            var launcher = new PortableUpdateLauncher(_installDir, _stateDirectory, update.File.Sha256, update.Version, _resultFile);
            await _stager.ApplyAsync(_drain, launcher, TimeSpan.FromSeconds(60), CancellationToken.None);
            _status.Text = "Restarting OrbitDrive…";
            await _quitForUpdate();              // the update script waits for every OrbitDrive program to exit, then swaps the folder and starts the new version
        }
        catch (UpdateRejectedException e) { _status.Text = e.Message; }
        catch (Exception e) when (e is IOException or InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception) { _status.Text = "The update could not be started: " + e.Message; }
        finally { _busy = false; _check.IsEnabled = true; _install.IsEnabled = true; }
    }
}
