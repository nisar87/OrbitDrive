using System.Text.Json;
using OrbitDrive.Protocol;

namespace OrbitDrive.Gateway;

public sealed class InvitationRecord
{
    public required string Id { get; init; }
    public required string OwnerUid { get; init; }
    public required string PcId { get; init; }
    public required string ReceiverId { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required long ModifiedUnixMs { get; init; }
    /// <summary>Opaque to everyone but the PC that issued it (sealed there). Never returned to a browser.</summary>
    public required string Grant { get; init; }
    public required string IdempotencyKey { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public InvitationState State { get; set; } = InvitationState.Pending;
    public string? Failure { get; set; }
}

/// <summary>
/// Durable "send to my phone" invitations. An invitation is an offer: it names one receiver (a paired browser), one file and an
/// expiry. It never contains file bytes and never claims a transfer happened; the file stays on the PC until the receiver accepts.
/// Stored in OrbitDrive:DataDirectory/invitations.json (atomically replaced) so a restart or reconnect shows the same Inbox.
/// </summary>
public sealed class Invitations
{
    private static readonly TimeSpan KeepFinished = TimeSpan.FromDays(7);
    private readonly GatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly Store _store;
    private readonly string? _file;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, InvitationRecord> _items = [];

    public Invitations(GatewayOptions options, TimeProvider time, Store store)
    {
        _options = options; _time = time; _store = store;
        if (options.DataDirectory.Length == 0) return;
        _file = Path.Join(options.DataDirectory, "invitations.json");
        if (!File.Exists(_file)) return;
        try
        {
            foreach (var dto in JsonSerializer.Deserialize<Dto[]>(File.ReadAllText(_file)) ?? throw new JsonException("empty"))
                _items[dto.Id] = new InvitationRecord
                {
                    Id = dto.Id, OwnerUid = dto.OwnerUid, PcId = dto.PcId, ReceiverId = dto.ReceiverId, Name = dto.Name, Size = dto.Size,
                    ModifiedUnixMs = dto.ModifiedUnixMs, Grant = dto.Grant, IdempotencyKey = dto.IdempotencyKey,
                    CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(dto.CreatedUnixMs), ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(dto.ExpiresUnixMs),
                    State = Enum.Parse<InvitationState>(dto.State), Failure = dto.Failure,
                };
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException)
        {
            _items.Clear();   // an unreadable file offers nothing; keep it for investigation
            File.Move(_file, _file + ".corrupt-" + time.GetUtcNow().ToUnixTimeSeconds(), overwrite: true);
        }
    }

    private sealed record Dto(string Id, string OwnerUid, string PcId, string ReceiverId, string Name, long Size, long ModifiedUnixMs, string Grant,
        string IdempotencyKey, long CreatedUnixMs, long ExpiresUnixMs, string State, string? Failure);

    private void Save()
    {
        if (_file is null) return;
        var cutoff = _time.GetUtcNow() - KeepFinished;
        foreach (var gone in _items.Values.Where(i => i.State != InvitationState.Pending && i.ExpiresAt < cutoff).ToList()) _items.Remove(gone.Id);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_items.Values.Select(i => new Dto(i.Id, i.OwnerUid, i.PcId, i.ReceiverId, i.Name, i.Size, i.ModifiedUnixMs, i.Grant,
            i.IdempotencyKey, i.CreatedAt.ToUnixTimeMilliseconds(), i.ExpiresAt.ToUnixTimeMilliseconds(), i.State.ToString(), i.Failure))));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _file, overwrite: true);
    }

    /// <summary>Applies the passage of time and a withdrawn receiver. Called with the gate held.</summary>
    private void Refresh(InvitationRecord item)
    {
        if (item.State != InvitationState.Pending) return;
        if (item.ExpiresAt <= _time.GetUtcNow()) { item.State = InvitationState.Expired; Save(); }
        else if (!_store.DevicesForPc(item.PcId).Any(d => d.Id == item.ReceiverId))
        { item.State = InvitationState.Cancelled; item.Failure = "The receiving browser was removed."; Save(); }
    }

    /// <summary>The PC offers one file to one of its paired browsers. A retry with the same key returns the same invitation.</summary>
    public InvitationRecord Create(string pcId, CreateInvitationRequest request)
    {
        if (request.Name.Length is 0 or > 255 || request.Size < 0 || request.Grant.Length is 0 or > 8192 || request.IdempotencyKey.Length is 0 or > 64)
            throw new ProtocolException(ErrorCode.InvalidRequest, "The invitation is not valid.");
        lock (_gate)
        {
            var existing = _items.Values.FirstOrDefault(i => i.PcId == pcId && i.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null) { Refresh(existing); return existing; }
            var receiver = _store.DevicesForPc(pcId).FirstOrDefault(d => d.Id == request.ReceiverId)
                ?? throw new ProtocolException(ErrorCode.NotFound, "That phone or browser is not paired with this PC.");
            foreach (var item in _items.Values.Where(i => i.ReceiverId == receiver.Id)) Refresh(item);
            if (_items.Values.Count(i => i.ReceiverId == receiver.Id && i.State == InvitationState.Pending) >= _options.MaxPendingInvitationsPerReceiver)
                throw new ProtocolException(ErrorCode.LimitExceeded, "That phone already has many files waiting. Ask its owner to accept or decline some first.");
            var now = _time.GetUtcNow();
            var created = new InvitationRecord
            {
                Id = Secrets.Token(12), OwnerUid = receiver.OwnerUid, PcId = pcId, ReceiverId = receiver.Id, Name = request.Name, Size = request.Size,
                ModifiedUnixMs = request.ModifiedUnixMs, Grant = request.Grant, IdempotencyKey = request.IdempotencyKey, CreatedAt = now,
                ExpiresAt = now.AddSeconds(_options.InvitationTtlSeconds),
            };
            _items[created.Id] = created;
            Save();
            return created;
        }
    }

    public List<InvitationRecord> ForReceiver(string receiverId)
    {
        lock (_gate)
        {
            var mine = _items.Values.Where(i => i.ReceiverId == receiverId).OrderByDescending(i => i.CreatedAt).Take(200).ToList();
            foreach (var item in mine) Refresh(item);
            return mine;
        }
    }

    public List<InvitationRecord> ForPc(string pcId)
    {
        lock (_gate)
        {
            var mine = _items.Values.Where(i => i.PcId == pcId).OrderByDescending(i => i.CreatedAt).Take(200).ToList();
            foreach (var item in mine) Refresh(item);
            return mine;
        }
    }

    /// <summary>The invitation, only if it belongs to this receiver. Other receivers (even of the same account) get "not found".</summary>
    public InvitationRecord ForReceiver(string receiverId, string id)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item) || item.ReceiverId != receiverId) throw new ProtocolException(ErrorCode.NotFound, "That file is not waiting for you.");
            Refresh(item);
            return item;
        }
    }

    /// <summary>Receiver-side decision. Accepting stays possible until expiry so an interrupted download can be started again.</summary>
    public void Decline(string receiverId, string id)
    {
        lock (_gate)
        {
            var item = ForReceiver(receiverId, id);
            if (item.State is InvitationState.Pending or InvitationState.Accepted) { item.State = InvitationState.Declined; Save(); }
            else throw new ProtocolException(ErrorCode.Conflict, "This offer is already " + item.State.ToString().ToLowerInvariant() + ".");
        }
    }

    public void Accepted(InvitationRecord item) { lock (_gate) { if (item.State == InvitationState.Pending) { item.State = InvitationState.Accepted; Save(); } } }

    /// <summary>The file could not be prepared (changed, moved, deleted): recoverable by sending it again.</summary>
    public void Failed(InvitationRecord item, string reason) { lock (_gate) { if (item.State is InvitationState.Pending or InvitationState.Accepted) { item.State = InvitationState.Cancelled; item.Failure = reason; Save(); } } }

    /// <summary>Sender-side withdrawal. Only the PC that made the offer can withdraw it.</summary>
    public void Cancel(string pcId, string id)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item) || item.PcId != pcId) throw new ProtocolException(ErrorCode.NotFound, "No such invitation from this PC.");
            Refresh(item);
            if (item.State != InvitationState.Pending) return;
            item.State = InvitationState.Cancelled; item.Failure = "The sender withdrew this file.";
            Save();
        }
    }

    public InvitationSummary Summarize(InvitationRecord item, bool sourceOnline) => new()
    {
        InvitationId = item.Id, PcId = item.PcId, PcName = _store.PcName(item.PcId), ReceiverId = item.ReceiverId,
        ReceiverName = _store.DevicesForPc(item.PcId).FirstOrDefault(d => d.Id == item.ReceiverId)?.Name ?? "",
        Name = item.Name, Size = item.Size, ModifiedUnixMs = item.ModifiedUnixMs, State = item.State, SourceOnline = sourceOnline,
        CreatedAtUnixMs = item.CreatedAt.ToUnixTimeMilliseconds(), ExpiresAtUnixMs = item.ExpiresAt.ToUnixTimeMilliseconds(), Failure = item.Failure,
    };
}
