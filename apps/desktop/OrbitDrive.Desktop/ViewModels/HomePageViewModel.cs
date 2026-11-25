using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using OrbitDrive.Core;
using OrbitDrive.Desktop.Controls;

namespace OrbitDrive.Desktop.ViewModels;

/// <summary>The page names buttons navigate to. One place, so renaming a page is one edit.</summary>
internal static class PageNames
{
    public const string Home = "Home";
    public const string Folders = "Shared folders";
    public const string Devices = "Devices";
    public const string Activity = "Activity";
    public const string Settings = "Settings";
}

internal sealed class TransferItem(string name, string detail, double percent, bool indeterminate)
{
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public double Percent { get; } = percent;
    public bool Indeterminate { get; } = indeterminate;
    public string Summary => $"{Name}, {Detail}";
}

internal sealed class ActivityItem(string title, string detail, bool isProblem)
{
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public bool IsProblem { get; } = isProblem;
    public string Summary => $"{Title}, {Detail}{(IsProblem ? ", a problem" : "")}";
}

/// <summary>
/// Home: is sharing on, paused or broken (and the one button that fixes it), what is happening right now, and what happened lately.
/// Everything is the agent's real report; where the agent says nothing the page says so instead of guessing.
/// </summary>
internal sealed class HomePageViewModel : PageViewModel
{
    private const int RecentCount = 5;
    private readonly ConsoleHost _host;
    private readonly UpdatePanel _updates;
    private readonly ActivityFeed _feed = new();
    private string _transfersShown = "";

    private string _headline = "Starting…", _detail = "", _glyph = "";
    private StatusKind _kind = StatusKind.Neutral;
    private bool _showPause, _showResume, _showStart, _showSetUp, _showUpdates;
    private string _foldersTile = "–", _devicesTile = "–";
    private bool _step1Done, _step2Done, _showGetStarted, _showApproval, _showMetered, _showUpdateNotice;
    private string _approvalMessage = "", _updateMessage = "";
    private bool _running;

    public HomePageViewModel(ConsoleHost host, Action<string> go, UpdatePanel updates)
    {
        _host = host;
        _updates = updates;
        PauseCommand = new AsyncRelayCommand(host.TogglePauseAsync);
        ResumeCommand = new AsyncRelayCommand(host.TogglePauseAsync);
        StartCommand = new RelayCommand(() => { host.Connection.StartExplicitly(); _ = host.RefreshAsync(); });
        SetUpCommand = new RelayCommand(() => go(PageNames.Settings));
        OpenUpdatesCommand = new RelayCommand(() => go(PageNames.Settings));
        ReviewApprovalCommand = new RelayCommand(() => go(PageNames.Devices));
        PairCommand = new AsyncRelayCommand(async () =>
        {
            go(PageNames.Devices);
            if (await host.RunAsync(client => client.SendAsync("pair")) is { } problem) host.Log(problem);
        });
        ShareFolderCommand = new RelayCommand(() => go(PageNames.Folders));
        OpenFoldersCommand = new RelayCommand(() => go(PageNames.Folders));
        OpenDevicesCommand = new RelayCommand(() => go(PageNames.Devices));
        SeeAllActivityCommand = new RelayCommand(() => go(PageNames.Activity));
        _feed.Changed += ShowRecent;
        updates.Changed += ShowUpdateNotice;
        host.AddStatusConsumer(OnStatusAsync);
        ShowUpdateNotice();
    }

    public override string Title => PageNames.Home;

    public IRelayCommand PauseCommand { get; }
    public IRelayCommand ResumeCommand { get; }
    public IRelayCommand StartCommand { get; }
    public IRelayCommand SetUpCommand { get; }
    public IRelayCommand OpenUpdatesCommand { get; }
    public IRelayCommand ReviewApprovalCommand { get; }
    public IRelayCommand PairCommand { get; }
    public IRelayCommand ShareFolderCommand { get; }
    public IRelayCommand OpenFoldersCommand { get; }
    public IRelayCommand OpenDevicesCommand { get; }
    public IRelayCommand SeeAllActivityCommand { get; }

    public ObservableCollection<TransferItem> ActiveTransfers { get; } = [];
    public ObservableCollection<ActivityItem> Recent { get; } = [];

    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public string StatusGlyph { get => _glyph; private set => SetProperty(ref _glyph, value); }
    public StatusKind Kind { get => _kind; private set => SetProperty(ref _kind, value); }
    public bool ShowPause { get => _showPause; private set => SetProperty(ref _showPause, value); }
    public bool ShowResume { get => _showResume; private set => SetProperty(ref _showResume, value); }
    public bool ShowStart { get => _showStart; private set => SetProperty(ref _showStart, value); }
    public bool ShowSetUp { get => _showSetUp; private set => SetProperty(ref _showSetUp, value); }
    public bool ShowUpdates { get => _showUpdates; private set => SetProperty(ref _showUpdates, value); }

    public string FoldersTile { get => _foldersTile; private set { if (SetProperty(ref _foldersTile, value)) OnPropertyChanged(nameof(FoldersTileName)); } }
    public string DevicesTile { get => _devicesTile; private set { if (SetProperty(ref _devicesTile, value)) OnPropertyChanged(nameof(DevicesTileName)); } }
    public string FoldersTileName => $"{FoldersTile} shared folders. Open Shared folders.";
    public string DevicesTileName => $"{DevicesTile} paired devices. Open Devices.";

    public bool Step1Done { get => _step1Done; private set { if (SetProperty(ref _step1Done, value)) NotifySteps(); } }
    public bool Step2Done { get => _step2Done; private set { if (SetProperty(ref _step2Done, value)) NotifySteps(); } }
    public bool Step1Next => !_step1Done;
    public bool Step2Next => _step1Done && !_step2Done;
    public bool Step2Later => !_step1Done && !_step2Done;
    public bool ShowGetStarted { get => _showGetStarted; private set => SetProperty(ref _showGetStarted, value); }

    public bool ShowApproval { get => _showApproval; private set { if (SetProperty(ref _showApproval, value)) RefreshAttention(); } }
    public string ApprovalMessage { get => _approvalMessage; private set => SetProperty(ref _approvalMessage, value); }
    public bool ShowMetered { get => _showMetered; private set { if (SetProperty(ref _showMetered, value)) RefreshAttention(); } }
    public string MeteredMessage => "Files you send from this PC count against this connection's data allowance.";
    public bool ShowUpdateBar { get => _showUpdateNotice; private set { if (SetProperty(ref _showUpdateNotice, value)) RefreshAttention(); } }

    /// <summary>True while something below the status card needs the owner (a device asking, an update, a metered connection).</summary>
    public bool NeedsAttention => _showApproval || _showUpdateNotice || _showMetered;
    /// <summary>The "all clear" line: sharing is running (on or paused) and nothing is waiting.</summary>
    public bool AllClear => _running && !NeedsAttention;

    private void RefreshAttention() { OnPropertyChanged(nameof(NeedsAttention)); OnPropertyChanged(nameof(AllClear)); }
    public string UpdateMessage { get => _updateMessage; private set => SetProperty(ref _updateMessage, value); }

    public bool HasActiveTransfers => ActiveTransfers.Count > 0;
    public bool HasRecent => Recent.Count > 0;
    public bool NoRecent => Recent.Count == 0;

    private void NotifySteps()
    {
        OnPropertyChanged(nameof(Step1Next));
        OnPropertyChanged(nameof(Step2Next));
        OnPropertyChanged(nameof(Step2Later));
    }

    protected override void OnActiveChanged(bool active)
    {
        if (active) _ = _host.RefreshAsync();   // fetch recent activity straight away instead of at the next tick
    }

    private void ShowUpdateNotice()
    {
        var version = _updates.AvailableVersion;
        ShowUpdateBar = version is not null;
        UpdateMessage = version is null ? "" : $"MyOrbitDrive {version} is ready. Open Settings to update.";
    }

    private async Task OnStatusAsync(ConsoleStatus? status)
    {
        if (status is null) ShowAgentDown();
        else ShowStatus(status);
        if (IsActive && _host.Connection.Client is { } client) await _feed.RefreshAsync(client, RecentCount);
    }

    /// <summary>No status: the agent is not reachable. Say why in the words the console has always used, and offer Start only when it is not already starting by itself.</summary>
    private void ShowAgentDown()
    {
        var connection = _host.Connection;
        (Headline, Detail, StatusGlyph, Kind) = connection.Phase switch
        {
            AgentPhase.KeepsStopping => ("MyOrbitDrive keeps stopping", $"MyOrbitDrive's background program stopped unexpectedly {connection.MaxRestarts + 1} times in a short while, so it was not started again. Nothing is being shared. Choose Start MyOrbitDrive to try again. If it keeps stopping, copy the details from Settings, Diagnostics.", "", StatusKind.Critical),
            AgentPhase.Stopped => ("MyOrbitDrive is stopped", "Nothing is being shared until you start it again. Choose Start MyOrbitDrive.", "", StatusKind.Critical),
            AgentPhase.Starting => ("Starting…", "Starting MyOrbitDrive's background program.", "", StatusKind.Neutral),
            _ => ("MyOrbitDrive isn't running", "The background program is not running, so nothing is being shared.", "", StatusKind.Critical),
        };
        _running = false;
        ShowPause = ShowResume = ShowSetUp = ShowUpdates = false;
        ShowStart = !connection.MayStart;
        // Nothing is known while the agent is down: do not leave the last known numbers on screen.
        FoldersTile = DevicesTile = "–";
        ShowGetStarted = ShowApproval = false;
        ShowMetered = _host.Policies.IsMetered;
        RefreshAttention();
        ClearTransfers();
        ClearRecent();
    }

    private void ShowStatus(ConsoleStatus status)
    {
        (Headline, Detail, StatusGlyph, Kind) = status.State switch
        {
            "online" => ("Sharing is on", "Your paired devices can browse the folders you share.", "", StatusKind.Good),
            "paused" => ("Sharing is paused", "Your devices can't see anything until you resume.", "", StatusKind.Neutral),
            "connecting" => ("Connecting…", "Connecting to the relay, the MyOrbitDrive server that passes files to your devices. Your devices can't connect until this finishes.", "", StatusKind.Warning),
            "update_required" => ("Update needed", "This version of MyOrbitDrive is no longer accepted by the service, so sharing is off. Open Settings, then Updates, and choose Update now.", "", StatusKind.Critical),
            "not_configured" => ("Not set up yet", "This copy of MyOrbitDrive has no relay address, so it cannot connect. Install the latest version from the MyOrbitDrive website. You can see what this copy uses under Settings, Diagnostics.", "", StatusKind.Neutral),
            _ => ("Not connected", "MyOrbitDrive can't reach the relay right now. Check that this PC is online. It keeps trying by itself.", "", StatusKind.Warning),
        };
        var running = status.State is "online" or "paused";
        _running = running;
        ShowPause = status.State == "online";
        ShowResume = status.State == "paused";
        ShowStart = false;
        ShowSetUp = status.State == "not_configured";
        ShowUpdates = status.State == "update_required";

        FoldersTile = status.Roots.Count.ToString(CultureInfo.InvariantCulture);
        var devices = _host.Devices;
        DevicesTile = devices is null ? "–" : devices.Count.ToString(CultureInfo.InvariantCulture);
        Step1Done = status.Roots.Count > 0;
        Step2Done = devices is { Count: > 0 };
        // Onboarding shows only while a step is still open; the device step is judged only once the device list is known, so it never flashes for an existing setup.
        ShowGetStarted = status.State != "not_configured" && (!Step1Done || (devices is not null && !Step2Done));

        ShowApproval = status.Approval is not null;
        ApprovalMessage = status.Approval is null ? "" : $"“{status.Approval.BrowserName}” is asking to connect. Check the code on the Devices page before you approve.";
        ShowMetered = _host.Policies.IsMetered;
        RefreshAttention();   // _running changed too
        ShowSending(status);
        if (!running) ClearRecent();
    }

    private void ShowSending(ConsoleStatus status)
    {
        var sending = status.Transfers.Where(t => t.Outcome == "sending").ToList();
        var signature = string.Join("|", sending.Select(t => $"{t.Name}:{t.Sent}:{t.Length}"));
        if (signature == _transfersShown) return;
        _transfersShown = signature;
        ActiveTransfers.Clear();
        foreach (var t in sending)
        {
            var known = t.Length > 0;
            var detail = known ? $"{ActivityPresenter.FormatBytes(t.Sent)} of {ActivityPresenter.FormatBytes(t.Length)}" : $"{ActivityPresenter.FormatBytes(t.Sent)} sent";
            ActiveTransfers.Add(new TransferItem(t.Name, detail, known ? Math.Min(100, 100.0 * t.Sent / t.Length) : 0, !known));
        }
        OnPropertyChanged(nameof(HasActiveTransfers));
    }

    private void ClearTransfers()
    {
        if (ActiveTransfers.Count == 0 && _transfersShown.Length == 0) return;
        _transfersShown = "";
        ActiveTransfers.Clear();
        OnPropertyChanged(nameof(HasActiveTransfers));
    }

    private void ShowRecent()
    {
        Recent.Clear();
        foreach (var entry in _feed.Entries.Take(RecentCount))
        {
            var row = ActivityPresenter.Present(entry);
            Recent.Add(new ActivityItem(row.Title, row.Detail.Length == 0 ? row.When : $"{row.When} · {row.Detail}", row.IsProblem));
        }
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(NoRecent));
    }

    private void ClearRecent()
    {
        if (Recent.Count == 0) return;
        Recent.Clear();
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(NoRecent));
    }
}
