using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

public readonly record struct ByteRange(long Start, long Length, bool Partial);

public static class RangeParser
{
    /// <summary>
    /// Single-range parser (RFC 9110 §14). Returns null for an unsatisfiable range (416). Malformed or
    /// multi-range headers are ignored and the full representation is served, as the RFC permits.
    /// </summary>
    public static ByteRange? Parse(string? header, long size)
    {
        var full = new ByteRange(0, size, false);
        if (string.IsNullOrWhiteSpace(header)) return full;
        header = header.Trim();
        if (header.Length > 64 || !header.StartsWith("bytes=", StringComparison.Ordinal) || header.Contains(',')) return full;
        var spec = header[6..];
        var dash = spec.IndexOf('-');
        if (dash < 0) return full;
        var first = spec[..dash];
        var last = spec[(dash + 1)..];
        static bool Digits(string s, out long value)
        {
            value = 0;
            return s.Length is > 0 and <= 19 && s.All(char.IsAsciiDigit) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        if (first.Length == 0)
        {
            if (!Digits(last, out var suffix)) return full;
            if (suffix == 0 || size == 0) return null;
            var length = Math.Min(suffix, size);
            return new ByteRange(size - length, length, true);
        }
        if (!Digits(first, out var start)) return full;
        long end = size - 1;
        if (last.Length > 0)
        {
            if (!Digits(last, out end) || end < start) return full;
            end = Math.Min(end, size - 1);
        }
        if (start >= size) return null;
        return new ByteRange(start, end - start + 1, true);
    }
}

/// <summary>A browser download waiting for the matching one-use agent uplink on this instance.</summary>
public sealed class PendingStream
{
    public required string Id { get; init; }
    public required byte[] UplinkTokenHash { get; init; }
    public required string OwnerUid { get; init; }
    public TaskCompletionSource<HttpContext> Arrival { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Claimed;
}

public sealed class StreamRegistry(GatewayOptions options)
{
    public const int RelayBufferBytes = 64 * 1024;
    private readonly ConcurrentDictionary<string, PendingStream> _streams = new();

    public (PendingStream Stream, string UplinkToken) Create(string ownerUid)
    {
        if (_streams.Values.Count(s => s.OwnerUid == ownerUid) >= options.MaxConcurrentStreamsPerOwner)
            throw new ProtocolException(ErrorCode.LimitExceeded, "Too many transfers are running. Wait for one to finish.");
        var token = Secrets.Token();
        var stream = new PendingStream { Id = Secrets.Token(16), UplinkTokenHash = Secrets.Hash(token), OwnerUid = ownerUid };
        _streams[stream.Id] = stream;
        return (stream, token);
    }

    public PendingStream? Find(string id) => _streams.GetValueOrDefault(id);

    public void Remove(PendingStream stream, bool success)
    {
        _streams.TryRemove(stream.Id, out _);
        stream.Done.TrySetResult(success);
        stream.Arrival.TrySetCanceled();
    }

    /// <summary>Forwards exactly <paramref name="length"/> bytes through one pooled buffer; each write awaits the receiver (backpressure).</summary>
    public static async Task RelayAsync(Stream source, Stream destination, long length, CancellationToken cancellation)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RelayBufferBytes);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(RelayBufferBytes, remaining)), cancellation);
                if (read == 0) throw new IOException("The PC stopped sending before the transfer finished.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellation);
                remaining -= read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
