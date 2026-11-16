using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

/// <summary>
/// Verifies Firebase Authentication ID tokens the way Firebase documents for servers without the Admin SDK:
/// RS256 signature against Google's published keys, audience = project id, issuer = securetoken/project id,
/// expiry/issued-at checks and a non-empty subject. Token revocation (which needs service credentials) is not
/// checked here yet; OrbitDrive's own device grants are revoked independently on every request.
/// </summary>
public sealed class FirebaseIdentityVerifier(GatewayOptions options, TimeProvider time, IHttpClientFactory http, ILogger<FirebaseIdentityVerifier> logger) : BackgroundService, IIdentityVerifier
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);
    private volatile Dictionary<string, RSA>? _keys;
    private readonly SemaphoreSlim _refreshNow = new(0, 1);
    private long _lastForcedRefresh;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                using var client = http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                using var response = await client.GetAsync(options.Firebase.KeysUrl, stopping);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(stopping));
                var keys = new Dictionary<string, RSA>();
                foreach (var key in document.RootElement.GetProperty("keys").EnumerateArray())
                {
                    if (key.GetProperty("kty").GetString() != "RSA") continue;
                    var rsa = RSA.Create();
                    rsa.ImportParameters(new RSAParameters { Modulus = Base64Url.DecodeFromChars(key.GetProperty("n").GetString()), Exponent = Base64Url.DecodeFromChars(key.GetProperty("e").GetString()) });
                    if (rsa.KeySize < 2048) continue;
                    keys[key.GetProperty("kid").GetString()!] = rsa;
                }
                if (keys.Count > 0) { _keys = keys; delay = response.Headers.CacheControl?.MaxAge is { } age && age > TimeSpan.FromMinutes(5) ? age : TimeSpan.FromMinutes(5); }
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or FormatException or CryptographicException or TaskCanceledException && !stopping.IsCancellationRequested)
            { logger.LogWarning("Could not refresh identity signing keys: {Reason}", e.GetType().Name); }
            try { await _refreshNow.WaitAsync(delay, stopping); } catch (OperationCanceledException) { break; }
        }
    }

    public Identity Verify(string? bearerToken)
    {
        var fail = new ProtocolException(ErrorCode.Unauthenticated, "Sign in again to continue.");
        if (bearerToken is null || bearerToken.Length > 8192) throw fail;
        var parts = bearerToken.Split('.');
        if (parts.Length != 3) throw fail;
        var keys = _keys ?? throw new ProtocolException(ErrorCode.Unavailable, "Sign-in verification is temporarily unavailable. Try again shortly.");
        try
        {
            using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256") throw fail; // never "none", never HMAC
            if (!keys.TryGetValue(header.RootElement.GetProperty("kid").GetString() ?? "", out var key))
            {
                var now = time.GetUtcNow().ToUnixTimeSeconds(); // a rotated key: refresh, at most once every 10 s
                var last = Interlocked.Read(ref _lastForcedRefresh);
                if (now - last >= 10 && Interlocked.CompareExchange(ref _lastForcedRefresh, now, last) == last && _refreshNow.CurrentCount == 0) _refreshNow.Release();
                throw fail;
            }
            if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw fail;

            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var claims = payload.RootElement;
            var current = time.GetUtcNow();
            DateTimeOffset Time(string name) => DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty(name).GetInt64());
            var subject = claims.GetProperty("sub").GetString();
            if (claims.GetProperty("aud").GetString() != options.Firebase.ProjectId ||
                claims.GetProperty("iss").GetString() != "https://securetoken.google.com/" + options.Firebase.ProjectId ||
                string.IsNullOrEmpty(subject) || subject.Length > 128 ||
                Time("exp") <= current || Time("iat") > current + Skew || Time("auth_time") > current + Skew) throw fail;
            var email = claims.TryGetProperty("email", out var emailClaim) ? emailClaim.GetString() ?? "" : "";
            var verified = claims.TryGetProperty("email_verified", out var verifiedClaim) && verifiedClaim.ValueKind == JsonValueKind.True;
            if (options.Firebase.RequireVerifiedEmail && (!verified || email.Length == 0))
                throw new ProtocolException(ErrorCode.Forbidden, "Verify your email address to continue. Check your inbox for the verification link.");
            return new Identity(subject, email);
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException or CryptographicException) { throw fail; }
    }
}
