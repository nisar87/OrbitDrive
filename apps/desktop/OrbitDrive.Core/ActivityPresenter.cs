using System.Globalization;

namespace OrbitDrive.Core;

/// <summary>One row of the History page, already in plain words.</summary>
public sealed record ActivityRow(string When, string Title, string Detail, bool IsProblem);

/// <summary>
/// Turns the agent's history entries (fixed-vocabulary kinds, counts, sizes) into plain-language rows for the History page and a
/// plain-text export. The history never holds names or paths, and this class adds nothing to it: it only maps words, sizes and times.
/// Unknown kinds are shown as a generic line rather than hidden, so nothing the agent recorded is silently dropped.
/// </summary>
public static class ActivityPresenter
{
    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["agent_started"] = "OrbitDrive started",
        ["agent_stopping"] = "OrbitDrive is stopping",
        ["agent_stopped"] = "OrbitDrive stopped",
        ["connected"] = "Connected to the relay",
        ["disconnected"] = "Lost the connection to the relay",
        ["update_required"] = "This version is too old for the service",
        ["pairing_started"] = "A pairing code was created",
        ["pairing_requested"] = "A browser asked to pair",
        ["pairing_finished"] = "Pairing finished",
        ["shares_changed"] = "Shared folders changed",
        ["browser_revoked"] = "A device's access was removed",
        ["download"] = "A file was sent to a browser",
        ["upload"] = "A file was received from a browser",
        ["upload_cancelled"] = "A receive was cancelled",
        ["upload_failed"] = "A receive failed",
        ["send_to_phone_created"] = "A Send to phone offer was created",
        ["send_to_phone_changed"] = "Send to phone offers changed",
        ["error"] = "Something went wrong",
    };

    private static readonly HashSet<string> Problems = new(StringComparer.Ordinal) { "disconnected", "update_required", "upload_failed", "error" };

    public static ActivityRow Present(ActivityEntry entry, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        var kind = entry.Kind ?? "";
        var title = Titles.TryGetValue(kind, out var known) ? known : "Other activity";
        var detail = Humanize(kind, entry.Detail ?? "");
        if (entry.Bytes is { } bytes) detail = detail.Length == 0 ? FormatBytes(bytes) : $"{detail} · {FormatBytes(bytes)}";
        return new ActivityRow(FormatTime(entry.Time, zone ?? TimeZoneInfo.Local, now ?? DateTimeOffset.UtcNow), title, detail, Problems.Contains(kind));
    }

    /// <summary>
    /// The agent records short fixed codes ("sent_to_relay", "result approved", "3 shared folders"). Known ones are shown in plain words; anything else
    /// is shown exactly as recorded, never guessed at.
    /// </summary>
    internal static string Humanize(string kind, string detail)
    {
        switch (kind, detail)
        {
            case ("download", "sent_to_relay"): return "Handed to the relay";
            case ("download", "cancelled"): return "Stopped before it finished";
            case ("pairing_finished", "result approved"): return "Approved";
            case ("pairing_finished", "result denied"): return "Denied";
            case ("pairing_finished", "result expired"): return "The code expired";
            case ("agent_started", "state online"): return "Online";
            case ("agent_started", "state connecting"): return "Connecting";
            case ("agent_started", "state paused"): return "Sharing paused";
            case ("agent_started", "state not_configured"): return "Not set up yet";
        }
        if (kind == "shares_changed" && detail.EndsWith(" shared folders", StringComparison.Ordinal)
            && int.TryParse(detail.AsSpan(0, detail.Length - " shared folders".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            return count == 1 ? "1 shared folder" : $"{count} shared folders";
        return detail;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "";
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes" : $"{value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>Today shows the time only; other days show the date too, so a long list stays readable.</summary>
    public static string FormatTime(DateTimeOffset time, TimeZoneInfo zone, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(time, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        return local.Date == today ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Plain-text copy for the owner to read or paste somewhere. Same rows, newest first, no extra data.</summary>
    public static string ToText(IEnumerable<ActivityEntry> entries, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        var lines = entries.Select(e => Present(e, zone, now)).Select(r => r.Detail.Length == 0 ? $"{r.When}  {r.Title}" : $"{r.When}  {r.Title} ({r.Detail})");
        return string.Join("\n", lines);
    }
}
