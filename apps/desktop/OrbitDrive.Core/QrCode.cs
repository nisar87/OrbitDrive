using System.Text;

namespace OrbitDrive.Core;

/// <summary>
/// Minimal QR Code encoder (ISO/IEC 18004): byte mode, error-correction level M, versions 1-10 (up to 213 bytes),
/// enough for a pairing link. Verified module-for-module against an independent encoder in tests/integration/qr.test.mjs.
/// </summary>
public sealed class QrCode
{
    // Level M per version: (error-correction codewords per block, blocks in group 1, data codewords in group-1 blocks, blocks in group 2).
    private static readonly (int Ec, int G1, int D1, int G2)[] Blocks =
    [
        (0, 0, 0, 0), (10, 1, 16, 0), (16, 1, 28, 0), (26, 1, 44, 0), (18, 2, 32, 0), (24, 2, 43, 0),
        (16, 4, 27, 0), (18, 4, 31, 0), (22, 2, 38, 2), (22, 3, 36, 2), (26, 4, 43, 1),
    ];
    private static readonly int[][] Alignment = [[], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50]];

    public int Version { get; }
    public int Mask { get; }
    public int Size { get; }
    private readonly bool[,] _modules;   // [y, x], true = dark
    private readonly bool[,] _function;

    public bool this[int x, int y] => _modules[y, x];

    public static int DataCodewords(int version) { var b = Blocks[version]; return b.G1 * b.D1 + b.G2 * (b.D1 + 1); }

    public static QrCode Encode(string text, int forceMask = -1)
    {
        var data = Encoding.UTF8.GetBytes(text);
        for (var version = 1; version <= 10; version++)
        {
            var countBits = version <= 9 ? 8 : 16;
            if (4 + countBits + data.Length * 8 <= DataCodewords(version) * 8) return new QrCode(version, data, forceMask);
        }
        throw new ArgumentException("Text is too long for a pairing QR code.", nameof(text));
    }

    private QrCode(int version, byte[] data, int forceMask)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _function = new bool[Size, Size];
        DrawFunctionPatterns();
        DrawCodewords(AddErrorCorrection(BuildDataCodewords(data)));
        var best = forceMask;
        if (best < 0)
        {
            var lowest = int.MaxValue;
            for (var mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask); DrawFormat(mask);
                var penalty = Penalty();
                if (penalty < lowest) { lowest = penalty; best = mask; }
                ApplyMask(mask); // XOR again to undo
            }
        }
        Mask = best;
        ApplyMask(best);
        DrawFormat(best);
    }

    private byte[] BuildDataCodewords(byte[] data)
    {
        var capacity = DataCodewords(Version);
        var bits = new List<bool>(capacity * 8);
        void Append(int value, int length) { for (var i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Append(0b0100, 4);
        Append(data.Length, Version <= 9 ? 8 : 16);
        foreach (var b in data) Append(b, 8);
        Append(0, Math.Min(4, capacity * 8 - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity * 8; pad ^= 0xEC ^ 0x11) Append(pad, 8);
        var result = new byte[capacity];
        for (var i = 0; i < bits.Count; i++) if (bits[i]) result[i >> 3] |= (byte)(0x80 >> (i & 7));
        return result;
    }

    private static byte Multiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; }
        return (byte)z;
    }

    private static byte[] ReedSolomonRemainder(ReadOnlySpan<byte> data, int degree)
    {
        var divisor = new byte[degree];
        divisor[degree - 1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                divisor[j] = Multiply(divisor[j], root);
                if (j + 1 < degree) divisor[j] ^= divisor[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        var result = new byte[degree];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, degree - 1);
            result[degree - 1] = 0;
            for (var i = 0; i < degree; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    private byte[] AddErrorCorrection(byte[] data)
    {
        var (ec, g1, d1, g2) = Blocks[Version];
        var blocks = new List<(byte[] Data, byte[] Ec)>();
        var offset = 0;
        for (var i = 0; i < g1 + g2; i++)
        {
            var length = i < g1 ? d1 : d1 + 1;
            var block = data.AsSpan(offset, length);
            blocks.Add((block.ToArray(), ReedSolomonRemainder(block, ec)));
            offset += length;
        }
        var result = new List<byte>();
        for (var i = 0; i <= d1; i++) foreach (var block in blocks) if (i < block.Data.Length) result.Add(block.Data[i]);
        for (var i = 0; i < ec; i++) foreach (var block in blocks) result.Add(block.Ec[i]);
        return [.. result];
    }

    private void SetFunction(int x, int y, bool dark) { _modules[y, x] = dark; _function[y, x] = true; }

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++) { SetFunction(6, i, i % 2 == 0); SetFunction(i, 6, i % 2 == 0); }
        foreach (var (cx, cy) in new[] { (3, 3), (Size - 4, 3), (3, Size - 4) })
            for (var dy = -4; dy <= 4; dy++)
                for (var dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy, distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (x >= 0 && x < Size && y >= 0 && y < Size) SetFunction(x, y, distance != 2 && distance != 4);
                }
        var positions = Alignment[Version];
        for (var i = 0; i < positions.Length; i++)
            for (var j = 0; j < positions.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == positions.Length - 1) || (i == positions.Length - 1 && j == 0)) continue;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                        SetFunction(positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        DrawFormat(0); // reserves the format area
        if (Version < 7) return;
        var remainder = Version;
        for (var i = 0; i < 12; i++) remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        var bits = (Version << 12) | remainder;
        for (var i = 0; i < 18; i++)
        {
            var dark = ((bits >> i) & 1) != 0;
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, dark);
            SetFunction(b, a, dark);
        }
    }

    private void DrawFormat(int mask)
    {
        var data = mask; // error-correction level M has format bits 00
        var remainder = data;
        for (var i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        var bits = ((data << 10) | remainder) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        for (var i = 0; i <= 5; i++) SetFunction(8, i, Bit(i));
        SetFunction(8, 7, Bit(6)); SetFunction(8, 8, Bit(7)); SetFunction(7, 8, Bit(8));
        for (var i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(i));
        for (var i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(i));
        SetFunction(8, Size - 8, true);
    }

    private void DrawCodewords(byte[] codewords)
    {
        var index = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vertical = 0; vertical < Size; vertical++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vertical : vertical;
                    if (_function[y, x] || index >= codewords.Length * 8) continue;
                    _modules[y, x] = ((codewords[index >> 3] >> (7 - (index & 7))) & 1) != 0;
                    index++;
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (_function[y, x]) continue;
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0, 5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0, _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                _modules[y, x] ^= invert;
            }
    }

    private int Penalty()
    {
        var result = 0;
        for (var pass = 0; pass < 2; pass++) // rows then columns: runs of 5+ and finder-like patterns
            for (var a = 0; a < Size; a++)
            {
                var run = 1;
                for (var b = 1; b < Size; b++)
                {
                    var current = pass == 0 ? _modules[a, b] : _modules[b, a];
                    var previous = pass == 0 ? _modules[a, b - 1] : _modules[b - 1, a];
                    if (current == previous) { run++; if (run == 5) result += 3; else if (run > 5) result++; } else run = 1;
                }
                for (var b = 0; b + 10 < Size; b++)
                {
                    bool At(int k) => pass == 0 ? _modules[a, b + k] : _modules[b + k, a];
                    var core = At(0) && !At(1) && At(2) && At(3) && At(4) && !At(5) && At(6);
                    if (core && !At(7) && !At(8) && !At(9) && !At(10)) result += 40;
                    var mirrored = At(4) && !At(5) && At(6) && At(7) && At(8) && !At(9) && At(10);
                    if (mirrored && !At(0) && !At(1) && !At(2) && !At(3)) result += 40;
                }
            }
        var dark = 0;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (_modules[y, x]) dark++;
                if (x + 1 < Size && y + 1 < Size && _modules[y, x] == _modules[y, x + 1] && _modules[y, x] == _modules[y + 1, x] && _modules[y, x] == _modules[y + 1, x + 1]) result += 3;
            }
        result += Math.Abs(dark * 20 - Size * Size * 10) / (Size * Size) * 10;
        return result;
    }

    /// <summary>Rows of '0'/'1' characters, top to bottom, without the quiet zone.</summary>
    public string[] ToRows()
    {
        var rows = new string[Size];
        for (var y = 0; y < Size; y++)
        {
            var row = new char[Size];
            for (var x = 0; x < Size; x++) row[x] = _modules[y, x] ? '1' : '0';
            rows[y] = new string(row);
        }
        return rows;
    }
}
