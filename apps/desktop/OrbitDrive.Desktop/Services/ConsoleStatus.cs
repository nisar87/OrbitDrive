using System.Globalization;
using System.Text.Json.Nodes;

namespace OrbitDrive.Desktop;

internal sealed record RootInfo(string RootId, string Name, string Path, bool Available, bool ShowHidden, bool UploadsEnabled);
internal sealed record TransferInfo(string Name, long Sent, long Length, long Offset, string Outcome, DateTimeOffset Started);
internal sealed record ApprovalInfo(string AccountLabel, string BrowserName, string ConfirmationCode, string? BrowserOrigin = null);
internal sealed record PairingInfo(string Code, string Url, DateTimeOffset? ExpiresAt);
internal sealed record DeviceInfo(string DeviceId, string Name, DateTimeOffset? LastSeen);

/// <summary>
/// The agent's status, typed. Parsed once per refresh from what the agent really reported; nothing is added or guessed. Pages read this instead of
/// picking JSON apart.
/// </summary>
internal sealed record ConsoleStatus(
    string State, bool Paused, string PcName, string PcId, string AgentVersion, string? Gateway, string? WebOrigin,
    int ActiveStreams, int MaxUploadKBps, int MaxConcurrentTransfers,
    IReadOnlyList<RootInfo> Roots, IReadOnlyList<TransferInfo> Transfers, ApprovalInfo? Approval, PairingInfo? Pairing)
{
    private static string Text(JsonNode? node) => node?.ToString() ?? "";
    private static long Number(JsonNode? node) => long.TryParse(Text(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static bool Flag(JsonNode? node) => bool.TryParse(Text(node), out var value) && value;
    private static DateTimeOffset? Moment(JsonNode? node) => Number(node) is > 0 and var ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    public static ConsoleStatus Parse(JsonObject status)
    {
        var roots = (status["roots"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(r => new RootInfo(Text(r["rootId"]), Text(r["name"]), Text(r["path"]), Flag(r["available"]), Flag(r["showHidden"]), Flag(r["uploadsEnabled"]))).ToList();
        var transfers = (status["transfers"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(t => new TransferInfo(Text(t["name"]), Number(t["sent"]), Number(t["length"]), Number(t["offset"]), Text(t["outcome"]),
                Moment(t["startedUnixMs"]) ?? DateTimeOffset.UnixEpoch)).ToList();
        var approval = status["pendingApproval"] is JsonObject a
            ? new ApprovalInfo(Text(a["accountLabel"]), Text(a["browserName"]), Text(a["confirmationCode"]), Optional(a["browserOrigin"])) : null;
        var pairing = status["activePairing"] is JsonObject p
            ? new PairingInfo(Text(p["code"]), Text(p["url"]), Moment(p["expiresAtUnixMs"])) : null;
        string? Optional(JsonNode? node) => Text(node) is { Length: > 0 } text ? text : null;
        return new ConsoleStatus(
            Text(status["state"]), Flag(status["paused"]), Text(status["pcName"]), Text(status["pcId"]), Text(status["agentVersion"]),
            Optional(status["gateway"]), Optional(status["webOrigin"]),
            (int)Number(status["activeStreams"]), (int)Number(status["maxUploadKBps"]), (int)Number(status["maxConcurrentTransfers"]),
            roots, transfers, approval, pairing);
    }

    public static IReadOnlyList<DeviceInfo> ParseDevices(JsonArray list) =>
        [.. list.OfType<JsonObject>().Select(d => new DeviceInfo(Text(d["deviceId"]), Text(d["name"]), Moment(d["lastSeenUnixMs"])))];
}
