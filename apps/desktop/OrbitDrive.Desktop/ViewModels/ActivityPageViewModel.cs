using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using OrbitDrive.Core;

namespace OrbitDrive.Desktop.ViewModels;

/// <summary>One file the agent has sent (or is sending) to the relay. The PC can see what it handed to the relay, not whether a browser saved it.</summary>
internal sealed class TransferRow(TransferInfo t)
{
    public string Name { get; } = t.Name;
    public bool IsSending { get; } = t.Outcome == "sending";
    public double Percent { get; } = t.Length > 0 ? Math.Min(100, 100.0 * t.Sent / t.Length) : 0;
    public bool Indeterminate => IsSending && t.Length <= 0;

    private string What => t.Outcome switch
    {
        "sending" => "being sent to the relay",
        "sent_to_relay" => "sent to the relay",
        "cancelled" => "stopped (cancelled, paused or access removed)",
        _ => "interrupted",
    };

    public string Detail =>
        $"{ActivityPresenter.FormatBytes(t.Sent)} of {ActivityPresenter.FormatBytes(t.Length)} {What}{(t.Offset == 0 ? "" : $" (continued from {ActivityPresenter.FormatBytes(t.Offset)})")}  ·  {t.Started.ToLocalTime():HH:mm:ss}";

    public string AutomationName => $"{Name}  —  {Detail}";
}

/// <summary>
/// Activity: what is being sent now, what OrbitDrive recorded on this PC (event kinds, counts and sizes only; the agent never stores file names, folders or
/// people, so this page cannot show them), and short notes from this session. Designed to read as complete with exactly that data.
/// </summary>
internal sealed class ActivityPageViewModel : PageViewModel
{
    private const int HistoryCount = 200, NotesCount = 200;
    private readonly ConsoleHost _host;
    private readonly ActivityFeed _feed = new();
    private readonly Func<string, string, bool> _confirm;
    private readonly Action<string> _copy;
    private string _transfersShown = "", _summary = "No transfers yet.", _error = "";

    public ActivityPageViewModel(ConsoleHost host, Func<string, string, bool> confirm, Action<string> copy)
    {
        _host = host; _confirm = confirm; _copy = copy;
        CopyCommand = new RelayCommand(CopyHistory);
        ClearCommand = new AsyncRelayCommand(ClearAsync);
        _feed.Changed += ShowHistory;
        host.Note += AddNote;
        host.AddStatusConsumer(OnStatus);
    }

    public override string Title => PageNames.Activity;

    public IRelayCommand CopyCommand { get; }
    public IRelayCommand ClearCommand { get; }

    public ObservableCollection<TransferRow> Transfers { get; } = [];
    public ObservableCollection<ActivityItem> History { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    public bool HasTransfers => Transfers.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool NoHistory => History.Count == 0;
    public bool HasNotes => Notes.Count > 0;
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    private void AddNote(string line)
    {
        Notes.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (Notes.Count > NotesCount) Notes.RemoveAt(Notes.Count - 1);   // bounded
        OnPropertyChanged(nameof(HasNotes));
    }

    private void CopyHistory()
    {
        try { _copy(ActivityPresenter.ToText(_feed.Entries)); Error = ""; }
        catch (System.Runtime.InteropServices.ExternalException) { Error = "Windows would not let OrbitDrive use the clipboard just now."; }
    }

    private async Task ClearAsync()
    {
        if (!_confirm("Clear history", "Delete the saved history on this PC?\n\nThis cannot be undone. Your shared folders, paired devices and settings are not affected.")) return;
        Error = await _host.RunAsync(client => client.SendAsync("clear_activity")) ?? "";
        if (_host.Connection.Client is { } client) await _feed.RefreshAsync(client, HistoryCount);
    }

    private async Task OnStatus(ConsoleStatus? status)
    {
        if (status is not null) ShowTransfers(status);
        if (IsActive && _host.Connection.Client is { } client) await _feed.RefreshAsync(client, HistoryCount);
    }

    protected override void OnActiveChanged(bool active)
    {
        if (active && _host.Connection.Client is { } client) _ = _feed.RefreshAsync(client, HistoryCount);
    }

    private void ShowTransfers(ConsoleStatus status)
    {
        Summary = status.ActiveStreams == 0
            ? (status.Transfers.Count == 0 ? "No transfers yet." : "No transfers are running. Recent ones are listed below.")
            : $"{status.ActiveStreams} file(s) are being sent to the relay right now.";
        var signature = string.Join("|", status.Transfers.Select(t => $"{t.Name};{t.Sent};{t.Length};{t.Offset};{t.Outcome};{t.Started:O}"));
        if (signature == _transfersShown) return;
        _transfersShown = signature;
        Transfers.Clear();
        foreach (var t in status.Transfers) Transfers.Add(new TransferRow(t));
        OnPropertyChanged(nameof(HasTransfers));
    }

    private void ShowHistory()
    {
        History.Clear();
        foreach (var entry in _feed.Entries)
        {
            var row = ActivityPresenter.Present(entry);
            History.Add(new ActivityItem(row.Title, row.Detail.Length == 0 ? row.When : $"{row.When} · {row.Detail}", row.IsProblem));
        }
        OnPropertyChanged(nameof(HasHistory)); OnPropertyChanged(nameof(NoHistory));
    }
}
