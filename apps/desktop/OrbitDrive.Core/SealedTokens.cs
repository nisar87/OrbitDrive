using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core;

/// <summary>Contents of an opaque entry/read/cursor token. Only the agent can read or mint these.</summary>
internal sealed record TokenBody
{
    [JsonPropertyName("t")] public required string Type { get; init; }          // "e" entry, "r" read, "c" cursor
    [JsonPropertyName("r")] public required string RootId { get; init; }
    [JsonPropertyName("s")] public required string[] Segments { get; init; }
    [JsonPropertyName("d")] public bool IsDirectory { get; init; }
    [JsonPropertyName("z")] public long Size { get; init; }
    [JsonPropertyName("m")] public long ModifiedTicks { get; init; }
    [JsonPropertyName("g")] public long ChangeStamp { get; init; }
    [JsonPropertyName("h")] public string? Sha256 { get; init; }
    [JsonPropertyName("o")] public int Offset { get; init; }
}

/// <summary>
/// AES-256-GCM sealing for IDs handed to the gateway and browser. A client can neither read the
/// relative path inside nor forge an ID for a path the agent never listed.
/// </summary>
public sealed class SealedTokens
{
    private const int MaxTokenChars = 8192;
    private static readonly byte[] Aad = "orbitdrive-token-v1"u8.ToArray();
    private readonly byte[] _key;

    public SealedTokens(byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("Token key must be 32 bytes.", nameof(key));
        _key = (byte[])key.Clone();
    }

    internal string Seal(TokenBody body)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(body);
        var output = new byte[12 + plain.Length + 16];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var gcm = new AesGcm(_key, 16);
        gcm.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(12, plain.Length), output.AsSpan(12 + plain.Length, 16), Aad);
        return Base64Url.EncodeToString(output);
    }

    internal TokenBody Unseal(string? token, string expectedType)
    {
        var invalid = new ProtocolException(ErrorCode.InvalidRequest, "Unrecognised or tampered identifier.");
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenChars) throw invalid;
        byte[] raw;
        try { raw = Base64Url.DecodeFromChars(token); }
        catch (FormatException) { throw invalid; }
        if (raw.Length < 12 + 16 + 2) throw invalid;
        var plain = new byte[raw.Length - 28];
        try
        {
            using var gcm = new AesGcm(_key, 16);
            gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(12, plain.Length), raw.AsSpan(12 + plain.Length, 16), plain, Aad);
        }
        catch (CryptographicException) { throw invalid; }
        TokenBody? body;
        try { body = JsonSerializer.Deserialize<TokenBody>(plain); }
        catch (JsonException) { throw invalid; }
        if (body is null || body.Type != expectedType || body.Segments is null || body.RootId is null) throw invalid;
        return body;
    }
}
