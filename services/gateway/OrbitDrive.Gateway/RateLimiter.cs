using System.Net;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

/// <summary>
/// Fixed-window attempt counter for guessable or expensive operations (pairing claims and polls). Memory is bounded:
/// expired windows are swept and, if the table is still full, new keys are refused rather than tracked without limit.
/// </summary>
public sealed class RateLimiter(TimeProvider time)
{
    private const int MaxKeys = 50_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _windows = [];

    /// <summary>Counts one attempt. Returns 0 when allowed, otherwise the number of seconds until the window reopens.</summary>
    public int Hit(string key, int limitPerMinute)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_windows.TryGetValue(key, out var window) && now - window.Start < Window)
            {
                if (window.Count >= limitPerMinute) return Math.Max(1, (int)Math.Ceiling((window.Start + Window - now).TotalSeconds));
                _windows[key] = (window.Start, window.Count + 1);
                return 0;
            }
            if (_windows.Count >= MaxKeys)
            {
                foreach (var stale in _windows.Where(w => now - w.Value.Start >= Window).Select(w => w.Key).ToList()) _windows.Remove(stale);
                if (_windows.Count >= MaxKeys) return (int)Window.TotalSeconds;
            }
            _windows[key] = (now, 1);
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
            if (IPAddress.TryParse(last, out var client)) return (client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client).ToString();
        }
        return peer.ToString();
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
