using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;

namespace OrbitDrive.Desktop.ViewModels;

/// <summary>One paired phone or browser. The agent reports a name and when it was last seen, nothing more.</summary>
internal sealed record DeviceItem(string Id, string Name, string LastSeenText)
{
    public string AutomationName => $"{Name}, {LastSeenText}";

    public static string Since(DateTimeOffset? seen, DateTimeOffset now)
    {
        if (seen is not { } when_) return "Not seen yet";
        var ago = now - when_;
        if (ago < TimeSpan.FromMinutes(1)) return "Last seen just now";
        if (ago < TimeSpan.FromHours(1)) return $"Last seen {(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromDays(1)) return $"Last seen {(int)ago.TotalHours} h ago";
        return $"Last seen {when_.LocalDateTime:d MMM yyyy}";
    }
}

/// <summary>
/// Devices: pair a new phone or browser (QR, short code, expiry countdown), approve or deny a request after checking the confirmation code,
/// and remove a paired device. Shows only what the agent reports.
/// </summary>
internal sealed class DevicesPageViewModel : PageViewModel
{
    private readonly ConsoleHost _host;
    private readonly Func<string, string, bool> _confirm;
    private readonly Action<string> _copy;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _devicesShown = "";
    private DeviceItem? _selected;
    private PairingInfo? _pairing;
    private string _qrFor = "";
    private BitmapSource? _qr;
    private ApprovalInfo? _approval;
    private string _error = "", _expiry = "";

    public DevicesPageViewModel(ConsoleHost host, Func<string, string, bool> confirm, Action<string> copy)
    {
        _host = host; _confirm = confirm; _copy = copy;
        PairCommand = new AsyncRelayCommand(PairAsync);
        ApproveCommand = new AsyncRelayCommand(() => AnswerAsync("approve"));
        DenyCommand = new AsyncRelayCommand(() => AnswerAsync("deny"));
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
        CopyLinkCommand = new RelayCommand(() => { if (_pairing is { Url.Length: > 0 } p) _copy(p.Url); });
        _clock.Tick += (_, _) => UpdateExpiry();
        host.AddStatusConsumer(OnStatus);
    }

    public override string Title => PageNames.Devices;

    public IRelayCommand PairCommand { get; }
    public IRelayCommand ApproveCommand { get; }
    public IRelayCommand DenyCommand { get; }
    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand CopyLinkCommand { get; }

    public ObservableCollection<DeviceItem> Devices { get; } = [];
    public bool HasDevices => Devices.Count > 0;
    public bool NoDevices => Devices.Count == 0;

    public DeviceItem? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(SelectedHeading));
        }
    }
    public bool HasSelection => _selected is not null;
    public string SelectedHeading => _selected is null ? "" : $"Settings for {_selected.Name}";

    public bool HasPairing => _pairing is not null;
    public bool NoPairing => _pairing is null;
    public string PairCode => _pairing?.Code ?? "";
    public string PairLink => _pairing?.Url ?? "";
    public BitmapSource? PairQr { get => _qr; private set => SetProperty(ref _qr, value); }
    public string Expiry { get => _expiry; private set => SetProperty(ref _expiry, value); }
    public string PairHelp => "Scan the QR code, or open the link, in the MyOrbitDrive web app on your phone. The code works once. Only use a code you created yourself just now.";

    public bool ShowApproval => _approval is not null;
    public bool NoApproval => _approval is null;
    public string ApprovalBrowser => _approval?.BrowserName ?? "";
    public string ApprovalAccount => _approval?.AccountLabel ?? "";
    /// <summary>Which web app address is asking, so the owner can tell their own site from a look-alike. Empty when the relay didn't say.</summary>
    public string ApprovalOrigin => _approval?.BrowserOrigin ?? "Not reported by the relay";
    public string ApprovalCode => _approval?.ConfirmationCode ?? "";

    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    protected override void OnActiveChanged(bool active) => _clock.IsEnabled = active && _pairing is not null;

    private async Task PairAsync() => Error = await _host.RunAsync(client => client.SendAsync("pair")) ?? "";

    private async Task AnswerAsync(string command) => Error = await _host.RunAsync(client => client.SendAsync(command)) ?? "";

    private async Task RemoveAsync()
    {
        if (_selected is not { } device) return;
        if (!_confirm("Remove this device", $"Remove “{device.Name}”?\n\nIt loses access to your shared folders right away and has to be paired again to come back.")) return;
        Error = await _host.RunAsync(client => client.SendAsync("revoke_device", new JsonObject { ["deviceId"] = device.Id })) ?? "";
    }

    private void UpdateExpiry()
    {
        if (_pairing?.ExpiresAt is not { } at) { Expiry = ""; return; }
        var left = at - DateTimeOffset.UtcNow;
        Expiry = left <= TimeSpan.Zero ? "This code has expired. Create a new one." : $"Expires in {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private Task OnStatus(ConsoleStatus? status)
    {
        if (status is null) return Task.CompletedTask;

        var pairing = status.Pairing;
        if ((pairing?.Url ?? "") != _qrFor) { _qrFor = pairing?.Url ?? ""; PairQr = _qrFor.Length == 0 ? null : QrImage.Render(_qrFor); }
        if (pairing != _pairing)
        {
            _pairing = pairing;
            OnPropertyChanged(nameof(HasPairing)); OnPropertyChanged(nameof(NoPairing)); OnPropertyChanged(nameof(PairCode)); OnPropertyChanged(nameof(PairLink));
            _clock.IsEnabled = IsActive && pairing is not null;
            UpdateExpiry();
        }

        if (status.Approval != _approval)
        {
            _approval = status.Approval;
            OnPropertyChanged(nameof(ShowApproval)); OnPropertyChanged(nameof(NoApproval));
            OnPropertyChanged(nameof(ApprovalBrowser)); OnPropertyChanged(nameof(ApprovalAccount)); OnPropertyChanged(nameof(ApprovalOrigin)); OnPropertyChanged(nameof(ApprovalCode));
        }

        if (_host.Devices is { } devices)
        {
            var now = DateTimeOffset.Now;
            var key = string.Join("|", devices.Select(d => $"{d.DeviceId};{d.Name};{DeviceItem.Since(d.LastSeen, now)}"));
            if (key != _devicesShown)
            {
                _devicesShown = key;
                var keep = _selected?.Id;
                Devices.Clear();
                foreach (var d in devices) Devices.Add(new DeviceItem(d.DeviceId, d.Name, DeviceItem.Since(d.LastSeen, now)));
                Selected = keep is null ? null : Devices.FirstOrDefault(d => d.Id == keep);
                OnPropertyChanged(nameof(HasDevices)); OnPropertyChanged(nameof(NoDevices));
            }
        }
        return Task.CompletedTask;
    }
}
