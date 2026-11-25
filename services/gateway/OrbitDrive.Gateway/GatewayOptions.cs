namespace OrbitDrive.Gateway;

public sealed class DevIdentityOptions
{
    public bool Enabled { get; set; }
    public string Secret { get; set; } = "";
}

public sealed class FirestoreOptions
{
    /// <summary>When set, durable control metadata lives in this Firestore project instead of a local file. Credentials come from GOOGLE_APPLICATION_CREDENTIALS (never from configuration), or from FIRESTORE_EMULATOR_HOST for tests.</summary>
    public string ProjectId { get; set; } = "";
    /// <summary>A folder holding the previous file store (registrations.json). Imported once when Firestore is still empty; never written to.</summary>
    public string ImportFromDataDirectory { get; set; } = "";
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
    /// <summary>
    /// Also ask Firebase whether the account is disabled/deleted or its sessions were revoked (Identity Toolkit accounts:lookup with the service account
    /// from GOOGLE_APPLICATION_CREDENTIALS), so those take effect within about a minute instead of when the one-hour token expires. Off by default.
    /// </summary>
    public bool CheckRevocation { get; set; }
    /// <summary>How long one answer from Firebase is reused (1 to 300 s). This is also the longest a revoked account keeps working.</summary>
    public int RevocationCacheSeconds { get; set; } = 60;
    public const string GoogleLookupBase = "https://identitytoolkit.googleapis.com";
    /// <summary>Only tests may point this away from Google (Production refuses it).</summary>
    public string AccountLookupBase { get; set; } = GoogleLookupBase;
    /// <summary>Tests only: a fixed bearer token instead of the service-account credential (Production refuses it).</summary>
    public string AccountLookupTestToken { get; set; } = "";
}

public sealed class GatewayOptions
{
    public FirebaseOptions Firebase { get; set; } = new();
    public string[] AllowedWebOrigins { get; set; } = [];
    public DevIdentityOptions DevIdentity { get; set; } = new();
    /// <summary>Where durable registrations and grants are kept. Empty = memory only (everything is forgotten on restart).</summary>
    public string DataDirectory { get; set; } = "";
    public FirestoreOptions Firestore { get; set; } = new();
    /// <summary>A browser's approval ends when it has not been used for this many days (about 1 second to 3650 days; fractions allow tests): it must be paired again.</summary>
    public double DeviceIdleDays { get; set; } = 90;
    /// <summary>A browser's secret is replaced, transparently, once it is this many days old (about 1 second to 365 days).</summary>
    public double DeviceRotateDays { get; set; } = 30;
    /// <summary>How long the previous secret keeps working after a rotation (1 to 10080 minutes).</summary>
    public double DeviceRotationGraceMinutes { get; set; } = 1440;
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
    /// <summary>How many times one download link may start a transfer per minute (a resumed or seeking download uses a handful; a script hammering a link uses hundreds).</summary>
    public int DownloadStartsPerCapabilityPerMinute { get; set; } = 120;
    /// <summary>How many transfers one download link may have running at once.</summary>
    public int MaxStreamsPerCapability { get; set; } = 4;
    /// <summary>
    /// Accounts (by verified user id) that may use the operator API. This is the only way to become an operator: it is set by whoever deploys
    /// the service, never by signing up first or by any request. Empty = nobody can administer.
    /// </summary>
    public string[] OperatorUids { get; set; } = [];
    /// <summary>An operator request must come from a sign-in at most this old (the person signed in recently, not merely holds a refreshed token).</summary>
    public int OperatorMaxAuthAgeSeconds { get; set; } = 900;
    /// <summary>Relay bytes one account may move in a UTC day (downloads plus upload pieces). 0 = no cap.</summary>
    public long MaxRelayBytesPerOwnerPerDay { get; set; }
    /// <summary>When true the plan limits (PCs, browsers, file size, files and data a day) are enforced. Off until the owner announces the start date; usage is counted either way.</summary>
    public bool PlanLimitsEnabled { get; set; }
    /// <summary>When all accounts together have relayed more than this many bytes today (UTC), the operator overview raises a cost alert. 0 turns it off.</summary>
    public long RelayBytesDailyAlert { get; set; }
    /// <summary>Archive preparations one account may start per minute across all its browsers (each one makes the PC read and zip files).</summary>
    public int ArchiveStartsPerOwnerPerMinute { get; set; } = 40;
    /// <summary>PCs reporting an older OrbitDrive version are refused with an update message. Empty = no minimum. An operator can change it while running.</summary>
    public string MinAgentVersion { get; set; } = "";
    public int MaxControlMessageBytes { get; set; } = 262144;
    /// <summary>How many control connections may be waiting to prove who they are at once (each is dropped after HandshakeSeconds). Extra attempts are refused before the connection is accepted.</summary>
    public int MaxPendingHandshakes { get; set; } = 64;
    /// <summary>How many of those waiting places one client address may hold, so one address cannot keep every PC out (F-002).</summary>
    public int MaxPendingHandshakesPerAddress { get; set; } = 4;
    /// <summary>How long a new control connection may stay silent before it is dropped (1 to 30 s).</summary>
    public int HandshakeSeconds { get; set; } = 4;
    /// <summary>Control connections one client address may open per minute (a small office with several PCs behind one address is well inside this).</summary>
    public int AgentConnectionsPerAddressPerMinute { get; set; } = 120;
    /// <summary>PCs the relay has never seen that one client address may introduce per minute.</summary>
    public int NewPcRegistrationsPerAddressPerMinute { get; set; } = 30;
    /// <summary>PCs that are not paired to an account are kept in memory only (never saved), up to this many, and forgotten after UnownedPcIdleMinutes without contact.</summary>
    public int MaxUnownedPcs { get; set; } = 2000;
    public int UnownedPcIdleMinutes { get; set; } = 30;
    /// <summary>
    /// When true an operator call is refused unless the sign-in used a second factor (the token's firebase.sign_in_second_factor claim). Off by default so
    /// switching it on is a deliberate step after the operator account has a second factor in Firebase; see docs/operations/relay.md.
    /// </summary>
    public bool RequireOperatorSecondFactor { get; set; }
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
            if (Firebase.AccountLookupBase != FirebaseOptions.GoogleLookupBase || Firebase.AccountLookupTestToken.Length > 0) throw new InvalidOperationException("Production must check accounts against Google with the service account.");
            if (!Firebase.RequireVerifiedEmail) throw new InvalidOperationException("Production requires verified email addresses.");
            if (AllowedWebOrigins.Length == 0 || AllowedWebOrigins.Any(o => !o.StartsWith("https://", StringComparison.Ordinal)))
                throw new InvalidOperationException("Production requires explicit https web origins.");
        }
        if (AgentHeartbeatSeconds is < 1 or > 300 || AgentLeaseSeconds < AgentHeartbeatSeconds * 2 || AgentLeaseSeconds > 900)
            throw new InvalidOperationException("AgentLeaseSeconds must be at least twice AgentHeartbeatSeconds (1-300 s) and at most 900 s.");
        if (DownloadStartsPerCapabilityPerMinute < 1 || MaxStreamsPerCapability < 1) throw new InvalidOperationException("Download link limits must be at least 1.");
        if (PairingClaimsPerMinute < 1 || PairingPollsPerMinute < 1) throw new InvalidOperationException("Pairing rate limits must be at least 1 per minute.");
        if (TrustedProxies.Any(p => !System.Net.IPAddress.TryParse(p, out _))) throw new InvalidOperationException("TrustedProxies must be IP addresses.");
        if (DevIdentity.Enabled && firebase) throw new InvalidOperationException("Configure either development identities or Firebase, not both.");
        if (DevIdentity.Enabled && DevIdentity.Secret.Length < 32)
            throw new InvalidOperationException("OrbitDrive:DevIdentity:Secret must be at least 32 characters.");
        if (!DevIdentity.Enabled && !firebase) throw new InvalidOperationException("No identity verifier is configured.");
        if (!(DeviceIdleDays is >= 0.00001 and <= 3650) || !(DeviceRotateDays is >= 0.00001 and <= 365) || !(DeviceRotationGraceMinutes is >= 0.01 and <= 10080)) throw new InvalidOperationException("Browser approval limits are out of range (DeviceIdleDays up to 3650, DeviceRotateDays up to 365, DeviceRotationGraceMinutes up to 10080).");
        if (Firebase.RevocationCacheSeconds is < 1 or > 300) throw new InvalidOperationException("OrbitDrive:Firebase:RevocationCacheSeconds must be between 1 and 300.");
        if (Firebase.CheckRevocation && !firebase) throw new InvalidOperationException("OrbitDrive:Firebase:CheckRevocation needs OrbitDrive:Firebase:ProjectId.");
        if (firebase && !System.Text.RegularExpressions.Regex.IsMatch(Firebase.ProjectId, "^[a-z0-9-]{4,40}$")) throw new InvalidOperationException("OrbitDrive:Firebase:ProjectId is not a valid project id.");
        if (Firestore.ProjectId.Length > 0)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Firestore.ProjectId, "^[a-z0-9-]{4,40}$")) throw new InvalidOperationException("OrbitDrive:Firestore:ProjectId is not a valid project id.");
            if (DataDirectory.Length > 0) throw new InvalidOperationException("Configure either OrbitDrive:DataDirectory or OrbitDrive:Firestore, not both: two copies of the grants could disagree.");
        }
        if (OperatorUids.Any(u => u.Length is 0 or > 128 || u.Any(char.IsWhiteSpace))) throw new InvalidOperationException("OperatorUids must be non-empty user ids without spaces.");
        if (OperatorMaxAuthAgeSeconds is < 60 or > 3600) throw new InvalidOperationException("OperatorMaxAuthAgeSeconds must be 60-3600.");
        if (MaxPendingHandshakes < 1 || MaxPendingHandshakesPerAddress < 1 || HandshakeSeconds is < 1 or > 30) throw new InvalidOperationException("MaxPendingHandshakes and MaxPendingHandshakesPerAddress must be at least 1 and HandshakeSeconds 1-30.");
        if (AgentConnectionsPerAddressPerMinute < 1 || NewPcRegistrationsPerAddressPerMinute < 1 || MaxUnownedPcs < 1 || UnownedPcIdleMinutes < 1) throw new InvalidOperationException("Agent connection limits must be at least 1.");
        if (RelayBytesDailyAlert < 0 || ArchiveStartsPerOwnerPerMinute < 1) throw new InvalidOperationException("RelayBytesDailyAlert cannot be negative and ArchiveStartsPerOwnerPerMinute must be at least 1.");
        if (MaxRelayBytesPerOwnerPerDay < 0) throw new InvalidOperationException("MaxRelayBytesPerOwnerPerDay cannot be negative.");
        if (MinAgentVersion.Length > 0 && !Version.TryParse(MinAgentVersion, out _)) throw new InvalidOperationException("MinAgentVersion must be a version such as 1.2.3.");
        foreach (var origin in AllowedWebOrigins)
            if (origin.Contains('*') || !Uri.TryCreate(origin, UriKind.Absolute, out _))
                throw new InvalidOperationException("AllowedWebOrigins must list exact origins; wildcards are not accepted.");
    }
}
