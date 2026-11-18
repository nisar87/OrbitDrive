namespace OrbitDrive.Gateway;

/// <summary>
/// Maintenance mode (W12). The operator creates an empty file named MAINTENANCE in <c>OrbitDrive:DataDirectory</c> (and deletes it to end maintenance); no
/// restart is needed, so nothing that is running is interrupted. While it exists, only NEW work is refused with 503 and Retry-After: pairing, new
/// downloads/archives/searches/uploads and accepting an invitation. Everything already started (byte transfers, upload pieces, status, finish, cancel, revoke,
/// sign-out) keeps working, so maintenance can never cut or corrupt a transfer in progress. /healthz still says the process is alive; /readyz says "maintenance".
/// </summary>
public sealed class MaintenanceMode
{
    private readonly string? _flag;
    private long _checkedAt;   // 0 = never looked
    private volatile bool _active;

    public MaintenanceMode(GatewayOptions options) => _flag = options.DataDirectory.Length == 0 ? null : Path.Join(options.DataDirectory, "MAINTENANCE");

    /// <summary>True while the flag file exists (looked at most once a second).</summary>
    public bool Active
    {
        get
        {
            if (_flag is null) return false;
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _checkedAt);
            if (last != 0 && now - last < 1000) return _active;
            Interlocked.Exchange(ref _checkedAt, now);
            try { _active = File.Exists(_flag); } catch (IOException) { /* keep the last answer */ }
            return _active;
        }
    }

    /// <summary>Requests that would start new work.</summary>
    public static bool IsNewWork(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)) return false;
        var path = request.Path.Value ?? "";
        if (path == "/v1/download-sessions") return true;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);   // v1, kind, id, action
        if (parts is ["v1", "pairing-requests", _, "claim"]) return true;
        if (parts is ["v1", "invitations", _, "accept"]) return true;
        return parts is ["v1", "pcs", _, "archives" or "searches" or "uploads"];
    }
}
