namespace OrbitDrive.Gateway;

public sealed class DevIdentityOptions
{
    public bool Enabled { get; set; }
    public string Secret { get; set; } = "";
}

public sealed class FirebaseOptions
{
    public const string GoogleKeysUrl = "https://www.googleapis.com/service_accounts/v1/jwk/securetoken@system.gserviceaccount.com";
    public string ProjectId { get; set; } = "";
    /// <summary>JWK set used to verify ID-token signatures. Only tests may point this away from Google.</summary>
    public string KeysUrl { get; set; } = GoogleKeysUrl;
    public bool RequireVerifiedEmail { get; set; } = true;
    /// <summary>Development only: accept the unsigned tokens issued by the local Firebase Auth emulator.</summary>
    public bool AcceptEmulatorTokens { get; set; }
}

public sealed class GatewayOptions
{
    public FirebaseOptions Firebase { get; set; } = new();
    public string[] AllowedWebOrigins { get; set; } = [];
    public DevIdentityOptions DevIdentity { get; set; } = new();
    /// <summary>Where durable registrations and grants are kept. Empty = memory only (everything is forgotten on restart).</summary>
    public string DataDirectory { get; set; } = "";
    public int PairingTtlSeconds { get; set; } = 120;
    /// <summary>Pairing-code attempts allowed per minute, counted separately per client address and per account.</summary>
    public int PairingClaimsPerMinute { get; set; } = 10;
    /// <summary>Pairing status polls allowed per minute per account (the site polls every 1.5 s while waiting for the PC).</summary>
    public int PairingPollsPerMinute { get; set; } = 90;
    /// <summary>Reverse proxies (IP addresses) whose X-Forwarded-For is believed. Empty = use the TCP peer address.</summary>
    public string[] TrustedProxies { get; set; } = [];
    public int DownloadSessionTtlSeconds { get; set; } = 3600;
    public int AgentRequestTimeoutSeconds { get; set; } = 15;
    public int PrepareTimeoutSeconds { get; set; } = 900;
    public int MaxConcurrentStreamsPerOwner { get; set; } = 4;
    /// <summary>Unexpired download sessions one browser may hold (each is a prepared file on a PC).</summary>
    public int MaxDownloadSessionsPerDevice { get; set; } = 64;
    public int MaxPreviewSessionsPerDevice { get; set; } = 512;
    public int PreviewSessionTtlSeconds { get; set; } = 900;      // thumbnails; media previews use the download lifetime
    public int MaxConcurrentPreviewStreamsPerOwner { get; set; } = 6;
    /// <summary>How long a "send to my phone" invitation stays open before it expires.</summary>
    public int InvitationTtlSeconds { get; set; } = 86400;
    /// <summary>Open invitations one receiver may have at a time (a hostile or buggy PC cannot flood an Inbox).</summary>
    public int MaxPendingInvitationsPerReceiver { get; set; } = 50;
    /// <summary>The largest piece of an upload the relay will carry in one request, and how long one piece may take end to end.</summary>
    public int MaxUploadChunkBytes { get; set; } = 8 * 1024 * 1024;
    public int UploadChunkTimeoutSeconds { get; set; } = 600;
    /// <summary>Uploads one browser may start per minute (a phone sending many small photos starts many).</summary>
    public int UploadStartsPerMinute { get; set; } = 300;
    /// <summary>
    /// Accounts (by verified user id) that may use the operator API. This is the only way to become an operator: it is set by whoever deploys
    /// the service, never by signing up first or by any request. Empty = nobody can administer.
    /// </summary>
    public string[] OperatorUids { get; set; } = [];
    /// <summary>An operator request must come from a sign-in at most this old (the person signed in recently, not merely holds a refreshed token).</summary>
    public int OperatorMaxAuthAgeSeconds { get; set; } = 900;
    /// <summary>Relay bytes one account may move in a UTC day (downloads plus upload pieces). 0 = no cap.</summary>
    public long MaxRelayBytesPerOwnerPerDay { get; set; }
    /// <summary>PCs reporting an older OrbitDrive version are refused with an update message. Empty = no minimum. An operator can change it while running.</summary>
    public string MinAgentVersion { get; set; } = "";
    public int MaxControlMessageBytes { get; set; } = 262144;
    /// <summary>How many control connections may be waiting to prove who they are at once (each is dropped after 10 seconds). Extra attempts are refused before the connection is accepted.</summary>
    public int MaxPendingHandshakes { get; set; } = 64;
    /// <summary>How often a connected PC must prove it is alive, and how long silence is tolerated before it is treated as offline.</summary>
    public int AgentHeartbeatSeconds { get; set; } = 10;
    public int AgentLeaseSeconds { get; set; } = 30;

    /// <summary>Fails closed: production never starts with development identities or without a real verifier.</summary>
    public void Validate(IHostEnvironment environment)
    {
        var firebase = Firebase.ProjectId.Length > 0;
        if (environment.IsProduction())
        {
            if (DevIdentity.Enabled) throw new InvalidOperationException("Development identities cannot be enabled in Production.");
            if (!firebase) throw new InvalidOperationException("No production identity verifier is configured. Set OrbitDrive:Firebase:ProjectId.");
            if (Firebase.KeysUrl != FirebaseOptions.GoogleKeysUrl) throw new InvalidOperationException("Production must verify tokens against Google's signing keys.");
            if (Firebase.AcceptEmulatorTokens) throw new InvalidOperationException("Auth-emulator tokens cannot be accepted in Production.");
            if (!Firebase.RequireVerifiedEmail) throw new InvalidOperationException("Production requires verified email addresses.");
            if (AllowedWebOrigins.Length == 0 || AllowedWebOrigins.Any(o => !o.StartsWith("https://", StringComparison.Ordinal)))
                throw new InvalidOperationException("Production requires explicit https web origins.");
        }
        if (AgentHeartbeatSeconds is < 1 or > 300 || AgentLeaseSeconds < AgentHeartbeatSeconds * 2 || AgentLeaseSeconds > 900)
            throw new InvalidOperationException("AgentLeaseSeconds must be at least twice AgentHeartbeatSeconds (1-300 s) and at most 900 s.");
        if (PairingClaimsPerMinute < 1 || PairingPollsPerMinute < 1) throw new InvalidOperationException("Pairing rate limits must be at least 1 per minute.");
        if (TrustedProxies.Any(p => !System.Net.IPAddress.TryParse(p, out _))) throw new InvalidOperationException("TrustedProxies must be IP addresses.");
        if (DevIdentity.Enabled && firebase) throw new InvalidOperationException("Configure either development identities or Firebase, not both.");
        if (DevIdentity.Enabled && DevIdentity.Secret.Length < 32)
            throw new InvalidOperationException("OrbitDrive:DevIdentity:Secret must be at least 32 characters.");
        if (!DevIdentity.Enabled && !firebase) throw new InvalidOperationException("No identity verifier is configured.");
        if (firebase && !System.Text.RegularExpressions.Regex.IsMatch(Firebase.ProjectId, "^[a-z0-9-]{4,40}$")) throw new InvalidOperationException("OrbitDrive:Firebase:ProjectId is not a valid project id.");
        if (OperatorUids.Any(u => u.Length is 0 or > 128 || u.Any(char.IsWhiteSpace))) throw new InvalidOperationException("OperatorUids must be non-empty user ids without spaces.");
        if (OperatorMaxAuthAgeSeconds is < 60 or > 3600) throw new InvalidOperationException("OperatorMaxAuthAgeSeconds must be 60-3600.");
        if (MaxPendingHandshakes < 1) throw new InvalidOperationException("MaxPendingHandshakes must be at least 1.");
        if (MaxRelayBytesPerOwnerPerDay < 0) throw new InvalidOperationException("MaxRelayBytesPerOwnerPerDay cannot be negative.");
        if (MinAgentVersion.Length > 0 && !Version.TryParse(MinAgentVersion, out _)) throw new InvalidOperationException("MinAgentVersion must be a version such as 1.2.3.");
        foreach (var origin in AllowedWebOrigins)
            if (origin.Contains('*') || !Uri.TryCreate(origin, UriKind.Absolute, out _))
                throw new InvalidOperationException("AllowedWebOrigins must list exact origins; wildcards are not accepted.");
    }
}
