using System.Buffers.Binary;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core;

/// <summary>
/// Reads the declared size of an image from its header without decoding a single pixel, so an image bomb (a tiny file that
/// claims to be 60,000 x 60,000) is refused before anything allocates memory for it.
/// </summary>
public static class ImageProbe
{
    public const long MaxPixels = 64_000_000;       // 64 megapixels, larger than any phone camera
    public const int MaxDimension = 30_000;
    public const int ProbeBytes = 256 * 1024;        // JPEG markers can sit behind large metadata blocks

    public static bool TryReadSize(ReadOnlySpan<byte> data, string contentType, out int width, out int height)
    {
        width = height = 0;
        try
        {
            return contentType switch
            {
                "image/png" => Png(data, out width, out height),
                "image/gif" => Gif(data, out width, out height),
                "image/jpeg" => Jpeg(data, out width, out height),
                "image/webp" => WebP(data, out width, out height),
                "image/bmp" => Bmp(data, out width, out height),
                _ => false,
            };
        }
        catch (ArgumentOutOfRangeException) { width = height = 0; return false; }
    }

    /// <summary>Throws LimitExceeded for an image that is too large to open safely, InvalidRequest for one whose header cannot be read.</summary>
    public static (int Width, int Height) Require(ReadOnlySpan<byte> data, string contentType)
    {
        if (!TryReadSize(data, contentType, out var w, out var h) || w <= 0 || h <= 0)
            throw new ProtocolException(ErrorCode.InvalidRequest, "This image could not be read, so no preview is shown.");
        if (w > MaxDimension || h > MaxDimension || (long)w * h > MaxPixels)
            throw new ProtocolException(ErrorCode.LimitExceeded, "This image is too large to preview safely. Download it instead.");
        return (w, h);
    }

    private static bool Png(ReadOnlySpan<byte> d, out int w, out int h)
    {
        w = h = 0;
        if (d.Length < 24 || !d.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        var rw = BinaryPrimitives.ReadUInt32BigEndian(d[16..]); var rh = BinaryPrimitives.ReadUInt32BigEndian(d[20..]);
        w = rw > int.MaxValue ? int.MaxValue : (int)rw; h = rh > int.MaxValue ? int.MaxValue : (int)rh;
        return true;
    }

    private static bool Gif(ReadOnlySpan<byte> d, out int w, out int h)
    {
        w = h = 0;
        if (d.Length < 10) return false;
        w = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]); h = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]);
        return true;
    }

    private static bool Bmp(ReadOnlySpan<byte> d, out int w, out int h)
    {
        w = h = 0;
        if (d.Length < 26) return false;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(d[14..]);
        if (headerSize == 12) { w = BinaryPrimitives.ReadUInt16LittleEndian(d[18..]); h = BinaryPrimitives.ReadUInt16LittleEndian(d[20..]); return true; }
        var rw = BinaryPrimitives.ReadInt32LittleEndian(d[18..]); var rh = BinaryPrimitives.ReadInt32LittleEndian(d[22..]);
        w = rw == int.MinValue ? int.MaxValue : Math.Abs(rw); h = rh == int.MinValue ? int.MaxValue : Math.Abs(rh);   // a negative height only means top-down
        return true;
    }

    private static bool WebP(ReadOnlySpan<byte> d, out int w, out int h)
    {
        w = h = 0;
        if (d.Length < 30) return false;
        var chunk = d.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8)) { w = 1 + (d[24] | d[25] << 8 | d[26] << 16); h = 1 + (d[27] | d[28] << 8 | d[29] << 16); return true; }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (d[20] != 0x2F) return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(d[21..]);
            w = (int)(bits & 0x3FFF) + 1; h = (int)((bits >> 14) & 0x3FFF) + 1;
            return true;
        }
        if (chunk.SequenceEqual("VP8 "u8))
        {
            if (d[23] != 0x9D || d[24] != 0x01 || d[25] != 0x2A) return false;
            w = BinaryPrimitives.ReadUInt16LittleEndian(d[26..]) & 0x3FFF; h = BinaryPrimitives.ReadUInt16LittleEndian(d[28..]) & 0x3FFF;
            return true;
        }
        return false;
    }

    private static bool Jpeg(ReadOnlySpan<byte> d, out int w, out int h)
    {
        w = h = 0;
        var i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }                                  // fill byte
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }   // markers without a length
            var length = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 2)..]);
            if (length < 2) return false;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)  // start-of-frame
            {
                if (i + 9 > d.Length) return false;
                h = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 5)..]); w = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 7)..]);
                return true;
            }
            i += 2 + length;
        }
        return false;
    }
}
