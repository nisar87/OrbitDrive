namespace OrbitDrive.Core;

public enum NoticeKind { PairingRequest, FilesSent }

/// <summary>
/// What a notification button may do. There is deliberately no Approve: the owner has to see the confirmation code in the console before approving, so a
/// notification can only refuse a request or open the console on it.
/// </summary>
public enum NoticeAction { Deny, Review }

public sealed record Notice(NoticeKind Kind, string Title, string Body, IReadOnlyList<NoticeAction> Actions);

/// <summary>The words and buttons of the console's notifications. Nothing here carries a file name, a folder, an account or the confirmation code.</summary>
public static class ConsoleNotices
{
    public const int MaxNameLength = 40;

    public static Notice PairingRequest(string? browserName) => new(
        NoticeKind.PairingRequest,
        "A device wants to connect",
        $"“{CleanName(browserName)}” is asking to connect to this PC. Open OrbitDrive and check the code before you approve.",
        [NoticeAction.Deny, NoticeAction.Review]);

    public static Notice FilesSent(int count) => new(
        NoticeKind.FilesSent,
        count == 1 ? "A file was sent" : $"{Math.Max(count, 2)} files were sent",
        "Handed to the relay for your device. OrbitDrive can't see whether it was saved.",
        []);

    /// <summary>A browser name comes from another machine: strip control characters, bound the length, never show an empty name.</summary>
    public static string CleanName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) return "A device";
        return cleaned.Length <= MaxNameLength ? cleaned : cleaned[..(MaxNameLength - 1)] + "…";
    }
}

/// <summary>Allows an event at most once per gap (so a burst of files gives one notification, not twenty).</summary>
public sealed class NoticeThrottle(TimeSpan gap, Func<DateTimeOffset> clock)
{
    private DateTimeOffset? _last;

    public bool Allow()
    {
        var now = clock();
        if (_last is { } last && now - last < gap) return false;
        _last = now;
        return true;
    }
}

/// <summary>
/// Turns a stream of status snapshots into notices. The first snapshot is only a baseline (starting the console never notifies about what was already
/// happening). A pairing request notifies once per confirmation code; a transfer notifies when it goes from sending to sent, batched by the throttle.
/// </summary>
public sealed class NoticeTracker(NoticeThrottle filesSentThrottle)
{
    private readonly Dictionary<string, string> _transfers = [];
    private string? _approvalCode;
    private bool _baseline;
    private int _pendingSent;

    public IReadOnlyList<Notice> Observe(string? approvalCode, string? approvalBrowser, IEnumerable<(string Key, string Outcome)> transfers)
    {
        var notices = new List<Notice>();

        if (approvalCode is not null && approvalCode != _approvalCode && _baseline) notices.Add(ConsoleNotices.PairingRequest(approvalBrowser));
        _approvalCode = approvalCode;

        foreach (var (key, outcome) in transfers)
        {
            if (_baseline && outcome == "sent_to_relay" && _transfers.TryGetValue(key, out var before) && before == "sending") _pendingSent++;
            _transfers[key] = outcome;
        }
        if (_pendingSent > 0 && filesSentThrottle.Allow()) { notices.Add(ConsoleNotices.FilesSent(_pendingSent)); _pendingSent = 0; }

        _baseline = true;
        return notices;
    }

    /// <summary>The request the owner has to answer, or null. A notification's Deny button must match this, so it never refuses a newer request by mistake.</summary>
    public string? PendingApprovalCode => _approvalCode;
}
