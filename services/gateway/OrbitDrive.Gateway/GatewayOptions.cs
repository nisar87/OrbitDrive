namespace OrbitDrive.Gateway;

public sealed class DevIdentityOptions
{
    public bool Enabled { get; set; }
    public string Secret { get; set; } = "";
}

public sealed class GatewayOptions
{
    public string[] AllowedWebOrigins { get; set; } = [];
    public DevIdentityOptions DevIdentity { get; set; } = new();
    public int PairingTtlSeconds { get; set; } = 120;
    public int DownloadSessionTtlSeconds { get; set; } = 3600;
    public int AgentRequestTimeoutSeconds { get; set; } = 15;
    public int PrepareTimeoutSeconds { get; set; } = 900;
    public int MaxConcurrentStreamsPerOwner { get; set; } = 4;
    public int MaxControlMessageBytes { get; set; } = 262144;

    /// <summary>Fails closed: production never starts with development identities or without a real verifier.</summary>
    public void Validate(IHostEnvironment environment)
    {
        if (environment.IsProduction())
        {
            if (DevIdentity.Enabled) throw new InvalidOperationException("Development identities cannot be enabled in Production.");
            throw new InvalidOperationException("No production identity verifier is configured. Firebase ID-token verification is delivered in phase W1; the gateway refuses to start in Production until then.");
        }
        if (DevIdentity.Enabled && DevIdentity.Secret.Length < 32)
            throw new InvalidOperationException("OrbitDrive:DevIdentity:Secret must be at least 32 characters.");
        if (!DevIdentity.Enabled) throw new InvalidOperationException("No identity verifier is configured.");
        foreach (var origin in AllowedWebOrigins)
            if (origin.Contains('*') || !Uri.TryCreate(origin, UriKind.Absolute, out _))
                throw new InvalidOperationException("AllowedWebOrigins must list exact origins; wildcards are not accepted.");
    }
}
