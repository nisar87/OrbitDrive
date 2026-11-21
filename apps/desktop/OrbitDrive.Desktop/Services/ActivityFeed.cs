using System.Globalization;
using System.Text.Json.Nodes;
using OrbitDrive.Core;
using OrbitDrive.Core.Ipc;

namespace OrbitDrive.Desktop;

/// <summary>
/// The agent's stored history, newest first. The agent only ever records fixed event kinds, counts and sizes (never file names, folders or people), so
/// neither this nor any page that shows it can show them. A page asks for as many entries as it wants; the feed says when something actually changed.
/// </summary>
internal sealed class ActivityFeed
{
    private string _shown = "";

    public IReadOnlyList<ActivityEntry> Entries { get; private set; } = [];
    public event Action? Changed;

    public async Task RefreshAsync(LocalIpcClient client, int count)
    {
        try
        {
            if (await client.SendAsync("activity", new JsonObject { ["count"] = count }) is not JsonObject reply || reply["entries"] is not JsonArray array) return;
            var signature = array.ToJsonString();
            if (signature == _shown) return;
            var entries = new List<ActivityEntry>();
            foreach (var node in array)
            {
                if (node is not JsonObject o || !DateTimeOffset.TryParse(o["time"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) continue;
                long? bytes = long.TryParse(o["bytes"]?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : null;
                entries.Add(new ActivityEntry(time, o["kind"]?.ToString() ?? "", o["detail"]?.ToString() ?? "", bytes));
            }
            _shown = signature;
            Entries = entries;
            Changed?.Invoke();
        }
        catch (Exception e) when (e is IpcException or TimeoutException) { }
    }
}
