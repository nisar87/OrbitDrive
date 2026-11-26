using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

/// <summary>Name is the display name the sign-in provider supplied (Firebase "name" claim), when there is one. AuthTime is when the person last actually signed in (not when the token was refreshed); null when the verifier cannot tell. SecondFactor is true when this sign-in used a second factor (Firebase's firebase.sign_in_second_factor claim).</summary>
public sealed record Identity(string Uid, string Email, DateTimeOffset? AuthTime = null, bool SecondFactor = false, string? Name = null);

public interface IIdentityVerifier
{
    /// <summary>Returns the verified account, or throws <see cref="ProtocolException"/> with Unauthenticated.</summary>
    Identity Verify(string? bearerToken);
}

/// <summary>
/// HMAC-signed development/test identities. Only registered outside Production (see GatewayOptions.Validate).
/// It stands in for Firebase ID-token verification until W1 and must never ship as the production verifier.
/// </summary>
public sealed class DevIdentityVerifier(GatewayOptions options, TimeProvider time) : IIdentityVerifier
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.DevIdentity.Secret);
    private sealed record Payload(string uid, string email, long exp, long auth = 0, bool mfa = false, string? name = null);

    /// <param name="signedInAgo">Test control: how long ago the person signed in (a refreshed token keeps the original sign-in time).</param>
    public string Issue(string email, TimeSpan lifetime, TimeSpan? signedInAgo = null, bool secondFactor = false, string? name = null)
    {
        var uid = "dev-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.ToLowerInvariant())))[..20];
        var payload = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(uid, email, time.GetUtcNow().Add(lifetime).ToUnixTimeSeconds(), time.GetUtcNow().Subtract(signedInAgo ?? TimeSpan.Zero).ToUnixTimeSeconds(), secondFactor, name)));
        return $"dev.{payload}.{Base64Url.EncodeToString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload)))}";
    }

    public static string UidFor(string token) => JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(token.Split('.')[1]))!.uid;

    public Identity Verify(string? bearerToken)
    {
        var fail = new ProtocolException(ErrorCode.Unauthenticated, "Sign in again to continue.");
        var parts = bearerToken?.Split('.');
        if (parts is not { Length: 3 } || parts[0] != "dev") throw fail;
        try
        {
            var expected = HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(expected, Base64Url.DecodeFromChars(parts[2]))) throw fail;
            var payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(parts[1]));
            if (payload is null || string.IsNullOrEmpty(payload.uid) || payload.exp <= time.GetUtcNow().ToUnixTimeSeconds()) throw fail;
            return new Identity(payload.uid, payload.email, payload.auth > 0 ? DateTimeOffset.FromUnixTimeSeconds(payload.auth) : null, payload.mfa, payload.name);
        }
        catch (Exception e) when (e is FormatException or JsonException) { throw fail; }
    }
}
