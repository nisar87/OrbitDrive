using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

/// <summary>What Firebase currently says about an account. ValidSinceSeconds is the moment before which sign-ins no longer count (set when sessions are revoked).</summary>
public sealed record AccountStatus(bool Exists, bool Disabled, long ValidSinceSeconds);

public interface IAccountStatusSource
{
    Task<AccountStatus> GetAsync(string uid, CancellationToken cancellation);
}

/// <summary>Identity Toolkit accounts:lookup with the service account. Reads one account's status; never lists accounts and never writes.</summary>
public sealed class IdentityToolkitAccountStatusSource(GatewayOptions options, IHttpClientFactory http) : IAccountStatusSource
{
    private readonly Lazy<GoogleCredential> _credential = new(() => GoogleCredential.GetApplicationDefault().CreateScoped("https://www.googleapis.com/auth/cloud-platform"));

    public async Task<AccountStatus> GetAsync(string uid, CancellationToken cancellation)
    {
        var token = options.Firebase.AccountLookupTestToken.Length > 0
            ? options.Firebase.AccountLookupTestToken
            : await ((ITokenAccess)_credential.Value).GetAccessTokenForRequestAsync(cancellationToken: cancellation);
        using var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Firebase.AccountLookupBase.TrimEnd('/')}/v1/projects/{options.Firebase.ProjectId}/accounts:lookup")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { localId = new[] { uid } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        if (!document.RootElement.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array || users.GetArrayLength() == 0) return new AccountStatus(false, false, 0);
        var user = users[0];
        var disabled = user.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True;
        var validSince = user.TryGetProperty("validSince", out var v) && long.TryParse(v.GetString(), out var seconds) ? seconds : 0;
        return new AccountStatus(true, disabled, validSince);
    }
}

/// <summary>
/// Decides whether a token that already passed signature and claim checks is still good according to Firebase: the account must exist, not be disabled,
/// and the person must have signed in after any "revoke sessions". Answers are cached for a minute. If Firebase cannot be asked, a previous answer up to
/// fifteen minutes old is used; with none, the request is refused as temporarily unavailable (never silently allowed).
/// </summary>
public sealed class RevocationGate(IAccountStatusSource source, TimeProvider time, ILogger<RevocationGate> logger, TimeSpan? freshFor = null)
{
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60), Stale = TimeSpan.FromMinutes(15);
    private readonly TimeSpan _fresh = freshFor ?? Fresh;
    private const int MaxEntries = 10_000;
    private readonly ConcurrentDictionary<string, (AccountStatus Status, DateTimeOffset At)> _cache = new();

    public void EnsureNotRevoked(string uid, DateTimeOffset authTime)
    {
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(uid, out var known) && now - known.At < _fresh) { Decide(known.Status, authTime); return; }
        AccountStatus status;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            status = source.GetAsync(uid, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or InvalidOperationException or IOException)
        {
            if (known.At != default && now - known.At < Stale)
            {
                logger.LogWarning("Could not ask Firebase about an account ({Reason}); using an answer from {Age:F0}s ago", e.GetType().Name, (now - known.At).TotalSeconds);
                Decide(known.Status, authTime);
                return;
            }
            logger.LogError("Could not ask Firebase about an account and have no recent answer ({Reason}); refusing the request", e.GetType().Name);
            throw new ProtocolException(ErrorCode.Unavailable, "Sign-in verification is temporarily unavailable. Try again shortly.");
        }
        if (_cache.Count >= MaxEntries) foreach (var stale in _cache.Where(e => now - e.Value.At >= _fresh).Select(e => e.Key).ToList()) _cache.TryRemove(stale, out _);
        if (_cache.Count < MaxEntries) _cache[uid] = (status, now);
        Decide(status, authTime);
    }

    private static void Decide(AccountStatus status, DateTimeOffset authTime)
    {
        if (!status.Exists || status.Disabled || authTime.ToUnixTimeSeconds() < status.ValidSinceSeconds)
            throw new ProtocolException(ErrorCode.Unauthenticated, "Sign in again to continue.");
    }
}
