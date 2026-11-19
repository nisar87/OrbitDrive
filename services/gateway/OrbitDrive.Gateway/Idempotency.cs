using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

/// <summary>
/// Idempotency keys for the requests that START work on a PC (new download, archive, search, upload). A phone on a weak connection retries when the answer is lost;
/// without this the PC would start the same work twice. A caller may send <c>Idempotency-Key: &lt;8..128 chars&gt;</c>:
/// the first request with that key runs normally and, when it succeeds (2xx), its answer is remembered for 24 hours; a retry with the same key and the same request
/// gets that same answer back (header <c>Idempotent-Replayed: true</c>) and does NOT reach the PC again; the same key with a different request is refused (409);
/// the same key while the first request is still running is refused (409 + Retry-After) rather than run twice. Failures are never remembered, so a retry after an
/// error runs again. Keys are private to one account AND one browser device. No key = behaviour unchanged. The memory is bounded (per account and in total) and is
/// not persisted: a gateway restart forgets keys (a retry after a restart can therefore run again; the PC-side upload/archive ids keep that safe for uploads).
/// </summary>
public sealed class IdempotencyStore(TimeProvider time)
{
    public const string Header = "Idempotency-Key";
    public const string ReplayHeader = "Idempotent-Replayed";
    public const int MaxStoredBody = 64 * 1024;
    private const int PerAccount = 200, Total = 20_000;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private sealed class Entry
    {
        public required string Owner { get; init; }
        public required byte[] Fingerprint { get; init; }
        public required DateTimeOffset Expires { get; set; }
        public bool Done { get; set; }
        public int Status { get; set; }
        public string ContentType { get; set; } = "";
        public byte[] Body { get; set; } = [];
    }

    public enum Outcome { Run, Replay, Mismatch, InFlight, Full }
    public readonly record struct Decision(Outcome Outcome, int Status = 0, string ContentType = "", byte[]? Body = null);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];

    /// <summary>The routes this applies to: POSTs that start work on a PC.</summary>
    public static bool Applies(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)) return false;
        var path = request.Path.Value ?? "";
        if (path == "/v1/download-sessions") return true;
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["v1", "pcs", _, "archives" or "searches" or "uploads"];
    }

    public static bool ValidKey(string key) => key.Length is >= 8 and <= 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    public static string Scope(string uid, string deviceHeader, string key) =>
        uid + "\n" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceHeader))) + "\n" + key;

    public static byte[] Fingerprint(string method, string path, byte[] body)
    {
        var head = Encoding.UTF8.GetBytes(method + "\n" + path + "\n");
        return SHA256.HashData([.. head, .. body]);
    }

    public Decision Begin(string scope, string owner, byte[] fingerprint)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_entries.TryGetValue(scope, out var found))
            {
                if (found.Expires > now)
                {
                    if (!CryptographicOperations.FixedTimeEquals(found.Fingerprint, fingerprint)) return new(Outcome.Mismatch);
                    return found.Done ? new(Outcome.Replay, found.Status, found.ContentType, found.Body) : new(Outcome.InFlight);
                }
                _entries.Remove(scope);
            }
            if (_entries.Count >= Total) Sweep(now);
            if (_entries.Count >= Total || _entries.Values.Count(e => e.Owner == owner) >= PerAccount)
            {
                SweepOwner(owner, now);
                if (_entries.Count >= Total || _entries.Values.Count(e => e.Owner == owner) >= PerAccount) return new(Outcome.Full);
            }
            _entries[scope] = new Entry { Owner = owner, Fingerprint = fingerprint, Expires = now + Lifetime };
            return new(Outcome.Run);
        }
    }

    public void Complete(string scope, int status, string contentType, byte[] body)
    {
        lock (_gate)
            if (_entries.TryGetValue(scope, out var entry)) { entry.Done = true; entry.Status = status; entry.ContentType = contentType; entry.Body = body; entry.Expires = time.GetUtcNow() + Lifetime; }
    }

    /// <summary>Forgets an unfinished or failed attempt so the retry runs again.</summary>
    public void Abandon(string scope) { lock (_gate) if (_entries.TryGetValue(scope, out var e) && !e.Done) _entries.Remove(scope); }

    private void Sweep(DateTimeOffset now) { foreach (var key in _entries.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _entries.Remove(key); }
    private void SweepOwner(string owner, DateTimeOffset now) { foreach (var key in _entries.Where(p => p.Value.Owner == owner && p.Value.Expires <= now).Select(p => p.Key).ToList()) _entries.Remove(key); }

    public int Count { get { lock (_gate) return _entries.Count; } }
}

/// <summary>Copies what the handler writes (up to a limit) while passing it on unchanged.</summary>
internal sealed class TeeStream(Stream inner, int limit) : Stream
{
    private readonly MemoryStream _copy = new();
    public bool Overflowed { get; private set; }
    public byte[] Captured => _copy.ToArray();
    private void Keep(ReadOnlySpan<byte> data)
    {
        if (Overflowed) return;
        if (_copy.Length + data.Length > limit) { Overflowed = true; _copy.SetLength(0); return; }
        _copy.Write(data);
    }
    public override void Write(byte[] buffer, int offset, int count) { Keep(buffer.AsSpan(offset, count)); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Keep(buffer); inner.Write(buffer); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Keep(buffer.Span); await inner.WriteAsync(buffer, cancellationToken); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override void Flush() => inner.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
