using System.Net;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

/// <summary>
/// Fixed-window attempt counter for guessable or expensive operations (pairing claims and polls). Memory is bounded: expired windows are swept and,
/// if a table is still full, new keys are refused rather than tracked without limit. There are two tables: one for keys anyone on the internet can
/// create (a client address, before any sign-in) and one for keys only a signed-in account can create. A flood of made-up addresses can therefore fill
/// only the first and never refuses a signed-in person (F-017).
/// </summary>
public sealed class RateLimiter(TimeProvider time)
{
    private const int MaxKeys = 50_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Table _anonymous = new(), _signedIn = new();

    private sealed class Table
    {
        public readonly Lock Gate = new();
        public readonly Dictionary<string, (DateTimeOffset Start, int Count)> Windows = [];
    }

    /// <summary>Counts one attempt. Returns 0 when allowed, otherwise the number of seconds until the window reopens. <paramref name="anonymous"/> marks a key made from a client address (no sign-in needed to create it).</summary>
    public int Hit(string key, int limitPerMinute, bool anonymous = false) => Hit(anonymous ? _anonymous : _signedIn, key, limitPerMinute);

    /// <summary>Entries in each table (tests and diagnostics).</summary>
    public (int Anonymous, int SignedIn) Counts { get { lock (_anonymous.Gate) lock (_signedIn.Gate) return (_anonymous.Windows.Count, _signedIn.Windows.Count); } }

    private int Hit(Table table, string key, int limitPerMinute)
    {
        var now = time.GetUtcNow();
        lock (table.Gate)
        {
            var windows = table.Windows;
            if (windows.TryGetValue(key, out var window) && now - window.Start < Window)
            {
                if (window.Count >= limitPerMinute) return Math.Max(1, (int)Math.Ceiling((window.Start + Window - now).TotalSeconds));
                windows[key] = (window.Start, window.Count + 1);
                return 0;
            }
            if (windows.Count >= MaxKeys)
            {
                foreach (var stale in windows.Where(w => now - w.Value.Start >= Window).Select(w => w.Key).ToList()) windows.Remove(stale);
                if (windows.Count >= MaxKeys) return (int)Window.TotalSeconds;
            }
            windows[key] = (now, 1);
            return 0;
        }
    }
}

public static class ClientAddress
{
    /// <summary>
    /// The address to rate-limit by. X-Forwarded-For is believed only when the TCP peer is a configured trusted proxy,
    /// and then only its right-most entry (the one that proxy appended); anything further left is client-controlled.
    /// </summary>
    public static string Of(HttpContext context, GatewayOptions options)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (peer is null) return "unknown";
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        if (options.TrustedProxies.Contains(peer.ToString()))
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
            var last = forwarded[(forwarded.LastIndexOf(',') + 1)..].Trim();
            if (IPAddress.TryParse(last, out var client)) return Normalize(client);
        }
        return Normalize(peer);
    }

    /// <summary>
    /// The key an address is limited by: an IPv4 address as it is; an IPv6 address by its /64 (one household or network holds a whole /64, so
    /// limiting single IPv6 addresses would let one client use billions of keys).
    /// </summary>
    public static string Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }
}

public static class DeviceNames
{
    /// <summary>Names are shown on the PC's approval screen and in device lists: short, visible, no control characters.</summary>
    public static string Clean(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is < 1 or > 64 || trimmed.Any(char.IsControl))
            throw new ProtocolException(ErrorCode.InvalidRequest, "Give this browser a short name (1-64 characters).");
        return trimmed;
    }
}
