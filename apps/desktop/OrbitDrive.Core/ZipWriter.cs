using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OrbitDrive.Core;

/// <summary>Raised when a source file does not deliver exactly the bytes it announced (it changed while being archived).</summary>
public sealed class ZipSourceChangedException(string message) : IOException(message);

/// <summary>
/// Minimal ZIP/ZIP64 writer for a SEEKABLE output (the private staging file). It never buffers a file: bytes are read in 1 MiB
/// pieces, hashed, optionally deflated, and written. Sizes and the CRC are patched into each entry's header afterwards, so there are
/// no data descriptors and every common unzip tool (and System.IO.Compression) reads the result. Entries that cannot be completed
/// are removed again, leaving the archive consistent. Names are UTF-8 (general-purpose flag 11).
/// </summary>
public sealed class ZipWriter : IDisposable
{
    private const uint Local = 0x04034b50, Central = 0x02014b50, EndOfCentral = 0x06054b50, End64 = 0x06064b50, End64Locator = 0x07064b50;
    private const uint Max32 = 0xFFFFFFFF;
    /// <summary>An entry whose source is at least this large gets ZIP64 fields up front (deflate can expand incompressible data slightly).</summary>
    public const long Zip64EntryThreshold = 0xF000_0000L;
    public const int CopyBuffer = 1 << 20;

    private sealed record Entry(byte[] Name, ushort Method, ushort Time, ushort Date, uint Crc, long Compressed, long Uncompressed, long Offset, bool Zip64, bool Directory);

    private readonly Stream _out;
    private readonly bool _forceZip64;
    private readonly List<Entry> _entries = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private bool _finished;

    public ZipWriter(Stream output, bool forceZip64 = false)
    {
        if (!output.CanSeek || !output.CanWrite) throw new ArgumentException("The archive is staged in a seekable file.", nameof(output));
        _out = output; _forceZip64 = forceZip64;
    }

    public int Count => _entries.Count;
    public long Length => _out.Position;

    private static (ushort Time, ushort Date) Dos(DateTime utc)
    {
        var local = utc.ToLocalTime();
        if (local.Year < 1980) local = new DateTime(1980, 1, 1, 0, 0, 0);
        if (local.Year > 2107) local = new DateTime(2107, 12, 31, 23, 59, 58);
        return ((ushort)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2)), (ushort)(((local.Year - 1980) << 9) | (local.Month << 5) | local.Day));
    }

    private byte[] NameBytes(string name, bool directory)
    {
        if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains("//", StringComparison.Ordinal) || name.Contains('\0'))
            throw new ArgumentException("Unsafe archive name.", nameof(name));
        foreach (var part in name.TrimEnd('/').Split('/'))
            if (part is "" or "." or "..") throw new ArgumentException("Unsafe archive name.", nameof(name));
        if (directory && !name.EndsWith('/')) name += "/";
        if (!directory && name.EndsWith('/')) throw new ArgumentException("A file name cannot end with a slash.", nameof(name));
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > 65535) throw new ArgumentException("Name too long.", nameof(name));
        if (!_names.Add(name)) throw new ArgumentException("Duplicate archive name.", nameof(name));
        return bytes;
    }

    public void AddDirectory(string name, DateTime modifiedUtc)
    {
        var bytes = NameBytes(name, true);
        var (time, date) = Dos(modifiedUtc);
        var offset = _out.Position;
        WriteLocal(bytes, 0, time, date, 0, 0, 0, zip64: false);
        _entries.Add(new Entry(bytes, 0, time, date, 0, 0, 0, offset, false, true));
    }

    private void WriteLocal(byte[] name, ushort method, ushort time, ushort date, uint crc, long compressed, long uncompressed, bool zip64)
    {
        var header = new byte[30 + name.Length + (zip64 ? 20 : 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Local);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)(zip64 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0x0800);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), method);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), time);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), date);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), zip64 ? Max32 : (uint)compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), zip64 ? Max32 : (uint)uncompressed);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)(zip64 ? 20 : 0));
        name.CopyTo(header.AsSpan(30));
        if (zip64)
        {
            var extra = header.AsSpan(30 + name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(extra, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
            BinaryPrimitives.WriteInt64LittleEndian(extra[4..], uncompressed);
            BinaryPrimitives.WriteInt64LittleEndian(extra[12..], compressed);
        }
        _out.Write(header);
    }

    /// <summary>
    /// Adds <paramref name="declaredSize"/> bytes read from <paramref name="source"/>. Throws <see cref="ZipSourceChangedException"/>
    /// (after removing the partial entry) if the source ends early or has more data than announced.
    /// </summary>
    public Task AddFileAsync(string name, DateTime modifiedUtc, SafeFileHandle source, long declaredSize, bool compress, Action<long>? progress, CancellationToken cancellation) =>
        AddCoreAsync(name, modifiedUtc, declaredSize, compress, (position, memory, token) => RandomAccess.ReadAsync(source, memory, position, token), () => RandomAccess.GetLength(source), progress, cancellation);

    private async Task AddCoreAsync(string name, DateTime modifiedUtc, long declaredSize, bool compress, Func<long, Memory<byte>, CancellationToken, ValueTask<int>> readAt, Func<long> currentLength, Action<long>? progress, CancellationToken cancellation)
    {
        var bytes = NameBytes(name, false);
        var (time, date) = Dos(modifiedUtc);
        var zip64 = _forceZip64 || declaredSize >= Zip64EntryThreshold;
        var start = _out.Position;
        var method = (ushort)(compress && declaredSize > 0 ? 8 : 0);
        var ok = false;
        try
        {
            WriteLocal(bytes, method, time, date, 0, 0, 0, zip64);
            var dataStart = _out.Position;
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBuffer);
            uint crc = 0;
            long read = 0;
            try
            {
                Stream sink = method == 8 ? new DeflateStream(_out, CompressionLevel.Fastest, leaveOpen: true) : new NonClosing(_out);
                await using (sink.ConfigureAwait(false))
                {
                    while (read < declaredSize)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var n = await readAt(read, buffer.AsMemory(0, (int)Math.Min(CopyBuffer, declaredSize - read)), cancellation).ConfigureAwait(false);
                        if (n == 0) throw new ZipSourceChangedException("The file became shorter while it was being archived.");
                        crc = Crc32.Update(crc, buffer.AsSpan(0, n));
                        await sink.WriteAsync(buffer.AsMemory(0, n), cancellation).ConfigureAwait(false);
                        read += n; progress?.Invoke(n);
                    }
                }
                if (currentLength() != declaredSize) throw new ZipSourceChangedException("The file changed while it was being archived.");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            var compressed = _out.Position - dataStart;
            var end = _out.Position;
            // Patch the real CRC and sizes into the header written above.
            _out.Position = start + 14;
            Span<byte> patch = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(patch, crc);
            BinaryPrimitives.WriteUInt32LittleEndian(patch[4..], zip64 ? Max32 : (uint)compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(patch[8..], zip64 ? Max32 : (uint)read);
            _out.Write(patch);
            if (zip64)
            {
                _out.Position = start + 30 + bytes.Length + 4;
                Span<byte> big = stackalloc byte[16];
                BinaryPrimitives.WriteInt64LittleEndian(big, read);
                BinaryPrimitives.WriteInt64LittleEndian(big[8..], compressed);
                _out.Write(big);
            }
            _out.Position = end;
            _entries.Add(new Entry(bytes, method, time, date, crc, compressed, read, start, zip64, false));
            ok = true;
        }
        finally
        {
            if (!ok)
            {
                _names.Remove(Encoding.UTF8.GetString(bytes));
                _out.SetLength(start); _out.Position = start;      // no half entry is ever left behind
            }
        }
    }

    /// <summary>Adds a small file whose content is already in memory (used for the omission report).</summary>
    public Task AddBytesAsync(string name, DateTime modifiedUtc, byte[] content, CancellationToken cancellation) =>
        AddCoreAsync(name, modifiedUtc, content.Length, true, (position, memory, _) => { var n = Math.Min(memory.Length, content.Length - (int)position); content.AsMemory((int)position, n).CopyTo(memory); return ValueTask.FromResult(n); }, () => content.Length, null, cancellation);

    public void Finish()
    {
        if (_finished) throw new InvalidOperationException();
        _finished = true;
        var centralStart = _out.Position;
        foreach (var e in _entries)
        {
            var needsSize = e.Zip64 || e.Uncompressed >= Max32 || e.Compressed >= Max32;
            var needsOffset = e.Offset >= Max32;
            var extraLength = (needsSize ? 16 : 0) + (needsOffset ? 8 : 0);
            var extra = extraLength > 0 ? extraLength + 4 : 0;
            var record = new byte[46 + e.Name.Length + extra];
            BinaryPrimitives.WriteUInt32LittleEndian(record, Central);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)(extra > 0 ? 45 : 20));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)(extra > 0 ? 45 : 20));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), 0x0800);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), e.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), e.Time);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), e.Date);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), e.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), needsSize ? Max32 : (uint)e.Compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), needsSize ? Max32 : (uint)e.Uncompressed);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), (ushort)e.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30), (ushort)extra);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(38), e.Directory ? 0x10u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), needsOffset ? Max32 : (uint)e.Offset);
            e.Name.CopyTo(record.AsSpan(46));
            if (extra > 0)
            {
                var span = record.AsSpan(46 + e.Name.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(span, 1);
                BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)extraLength);
                var at = 4;
                if (needsSize) { BinaryPrimitives.WriteInt64LittleEndian(span[at..], e.Uncompressed); BinaryPrimitives.WriteInt64LittleEndian(span[(at + 8)..], e.Compressed); at += 16; }
                if (needsOffset) BinaryPrimitives.WriteInt64LittleEndian(span[at..], e.Offset);
            }
            _out.Write(record);
        }
        var centralSize = _out.Position - centralStart;
        var count = _entries.Count;
        var big = _forceZip64 || count >= 0xFFFF || centralStart >= Max32 || centralSize >= Max32;
        if (big)
        {
            var end64Offset = _out.Position;
            Span<byte> r = stackalloc byte[56];
            BinaryPrimitives.WriteUInt32LittleEndian(r, End64);
            BinaryPrimitives.WriteInt64LittleEndian(r[4..], 44);
            BinaryPrimitives.WriteUInt16LittleEndian(r[12..], 45);
            BinaryPrimitives.WriteUInt16LittleEndian(r[14..], 45);
            BinaryPrimitives.WriteUInt32LittleEndian(r[16..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(r[20..], 0);
            BinaryPrimitives.WriteInt64LittleEndian(r[24..], count);
            BinaryPrimitives.WriteInt64LittleEndian(r[32..], count);
            BinaryPrimitives.WriteInt64LittleEndian(r[40..], centralSize);
            BinaryPrimitives.WriteInt64LittleEndian(r[48..], centralStart);
            _out.Write(r);
            Span<byte> loc = stackalloc byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(loc, End64Locator);
            BinaryPrimitives.WriteUInt32LittleEndian(loc[4..], 0);
            BinaryPrimitives.WriteInt64LittleEndian(loc[8..], end64Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(loc[16..], 1);
            _out.Write(loc);
        }
        Span<byte> end = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, EndOfCentral);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], big ? (ushort)0xFFFF : (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], big ? (ushort)0xFFFF : (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], big ? Max32 : (uint)centralSize);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], big ? Max32 : (uint)centralStart);
        _out.Write(end);
        _out.Flush();
    }

    /// <summary>Removes the entry added last (its bytes are cut off the staged file). Used when the source proved to have changed.</summary>
    public void RemoveLast()
    {
        var last = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        _names.Remove(Encoding.UTF8.GetString(last.Name));
        _out.SetLength(last.Offset); _out.Position = last.Offset;
    }

    public void Dispose() { }

    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    }
}

/// <summary>CRC-32 (IEEE) with slicing-by-8; the framework has no built-in one.</summary>
internal static class Crc32
{
    private static readonly uint[][] Tables = Build();

    private static uint[][] Build()
    {
        var t = new uint[8][];
        for (var i = 0; i < 8; i++) t[i] = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[0][n] = c;
        }
        for (var n = 0; n < 256; n++)
            for (var k = 1; k < 8; k++) t[k][n] = (t[k - 1][n] >> 8) ^ t[0][t[k - 1][n] & 0xFF];
        return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var c = ~crc;
        var i = 0;
        var t = Tables;
        for (; i + 8 <= data.Length; i += 8)
        {
            var lo = c ^ BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            var hi = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            c = t[7][lo & 0xFF] ^ t[6][(lo >> 8) & 0xFF] ^ t[5][(lo >> 16) & 0xFF] ^ t[4][lo >> 24] ^
                t[3][hi & 0xFF] ^ t[2][(hi >> 8) & 0xFF] ^ t[1][(hi >> 16) & 0xFF] ^ t[0][hi >> 24];
        }
        for (; i < data.Length; i++) c = t[0][(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return ~c;
    }
}
