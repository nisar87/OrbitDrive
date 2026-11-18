using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrbitDrive.Core;

namespace OrbitDrive.Agent;

/// <summary>
/// Turns the agent's lifecycle events into history entries. Every event goes through one place (Program.Emit), so every event is seen exactly once.
/// Only opaque facts are read from an event (state words, counts, byte totals). Names, folders and people are never looked at, and the
/// history cleans whatever it is given anyway.
/// </summary>
internal static class ActivityEvents
{
    public static void Record(ActivityLog? log, int sharedFolders, string name, object? data)
    {
        if (log is null) return;
        try
        {
            var node = data is null ? null : JsonSerializer.SerializeToNode(data) as JsonObject;
            string Field(string key) => node?[key]?.ToString() ?? "";
            long? Bytes(string key) => long.TryParse(Field(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
            switch (name)
            {
                case "ready": log.Record("agent_started", $"state {Field("state")}"); break;
                case "quitting": log.Record("agent_stopping"); break;
                case "stopped": log.Record("agent_stopped"); break;
                case "connected": case "disconnected": case "update_required": log.Record(name); break;
                case "pairing": log.Record("pairing_started"); break;
                case "pairing_claimed": log.Record("pairing_requested", "a browser asked to pair; waiting for the owner"); break;
                case "pairing_result": log.Record("pairing_finished", $"result {Field("state")}"); break;
                case "roots_changed": case "roots_reloaded": log.Record("shares_changed", $"{sharedFolders} shared folders"); break;
                case "device_revoked": log.Record("browser_revoked"); break;
                case "stream_finished": log.Record("download", Field("outcome"), Bytes("bytes")); break;
                case "upload_finished": log.Record("upload", $"state {Field("state")}", Bytes("bytes")); break;
                case "upload_cancelled": log.Record("upload_cancelled"); break;
                case "upload_failed": log.Record("upload_failed", $"code {Field("code")}"); break;
                case "invitation_created": log.Record("send_to_phone_created"); break;
                case "offers_changed": log.Record("send_to_phone_changed"); break;
                case "error": log.Record("error", Field("code") is { Length: > 0 } code ? $"{code}: {Field("message")}" : Field("message")); break;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { /* diagnostics never break the app */ }
    }
}
