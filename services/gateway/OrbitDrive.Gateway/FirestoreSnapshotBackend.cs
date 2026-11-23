using System.Text.Json;
using Google.Cloud.Firestore;

namespace OrbitDrive.Gateway;

/// <summary>
/// Durable control metadata in Cloud Firestore: one document per PC (pcs/{pcId}), per approved or revoked browser (devices/{id}) and per account with a
/// preference or suspension (owners/{uid}). Never file names, file bodies, directory listings or per-packet progress.
/// The gateway is the only reader/writer (server credentials bypass security rules, so the database is created in production mode and the Store enforces ownership).
/// Writes are write-behind: the Store's in-memory state is authoritative (a revocation takes effect immediately) and the latest state is flushed in the background,
/// only changed documents, with retry. <see cref="FlushAsync"/> waits until everything accepted so far is stored; it also runs on shutdown.
/// </summary>
public sealed class FirestoreSnapshotBackend : ISnapshotBackend, IHostedService, IAsyncDisposable
{
    private const string Pcs = "pcs", Devices = "devices", Owners = "owners", Meta = "meta";
    private readonly FirestoreDb _db;
    private readonly ILogger? _log;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _retryDelay;
    private readonly ISnapshotBackend? _legacy;
    private Task? _worker;
    private Store.Snapshot? _pending;
    private bool _writing;
    private long _acceptedGeneration, _storedGeneration;
    private Dictionary<string, string> _stored = [];   // document path -> content fingerprint of what Firestore currently holds
    private Exception? _lastError;
    private bool _closed;                      // set once shutdown starts: later saves are refused (logged), never queued for a worker that is going away
    private readonly TimeSpan? _leaseDuration;          // null: no single-writer lease (tests that deliberately overlap two backends)
    private readonly Action? _onLeaseLost;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private long _leaseEpoch;
    private bool _leaseLost;
    private Task? _renewer;
    private readonly Lazy<Task> _dispose;      // shutdown runs exactly once however many callers (or container registrations) ask for it

    /// <param name="legacy">The previous file backend: when Firestore holds nothing yet, its content is imported once (so switching backends never drops existing pairings).</param>
    /// <param name="writerLease">When set, this instance must hold the database's single-writer lease (meta/writer) before it loads anything: a SECOND live gateway on the same
    /// database refuses to start instead of racing the first one and overwriting its state. The lease is renewed every third of its length and given back on clean shutdown.</param>
    /// <param name="onLeaseLost">Called once if the lease is taken over (this instance stalled longer than the lease). The instance stops writing at once; the host should stop.</param>
    public FirestoreSnapshotBackend(FirestoreDb db, TimeProvider time, ILogger? log = null, TimeSpan? retryDelay = null, ISnapshotBackend? legacy = null, TimeSpan? writerLease = null, Action? onLeaseLost = null)
    {
        _db = db; _time = time; _log = log; _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2); _legacy = legacy; _leaseDuration = writerLease; _onLeaseLost = onLeaseLost;
        _dispose = new Lazy<Task>(ShutDownAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Creates the database client. Uses GOOGLE_APPLICATION_CREDENTIALS, or the emulator when FIRESTORE_EMULATOR_HOST is set.</summary>
    public static FirestoreDb Connect(string projectId) =>
        new FirestoreDbBuilder { ProjectId = projectId, EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOrProduction }.Build();

    // ---- loading -------------------------------------------------------------------------------------------------------------------------------

    public Store.Snapshot? Load()
    {
        AcquireWriterLease();
        var pcs = _db.Collection(Pcs).GetSnapshotAsync().GetAwaiter().GetResult();
        var devices = _db.Collection(Devices).GetSnapshotAsync().GetAwaiter().GetResult();
        var owners = _db.Collection(Owners).GetSnapshotAsync().GetAwaiter().GetResult();
        if (pcs.Count == 0 && devices.Count == 0 && owners.Count == 0)
        {
            StartWorker();
            if (_legacy?.Load() is { } imported)
            {
                _log?.LogWarning("Firestore is empty: importing {Pcs} PCs and {Devices} browsers from the previous file store", imported.Pcs.Length, imported.Devices.Length);
                Save(imported);
                return imported;
            }
            return null;
        }

        var snapshot = new Store.Snapshot(1,
            [.. pcs.Select(d => new Store.PcDto(d.Id, Text(d, "spki"), Text(d, "name"), Text(d, "agentVersion"), d.ContainsField("ownerUid") ? d.GetValue<string?>("ownerUid") : null, d.GetValue<long>("lastSeenUnixMs")))],
            [.. devices.Select(d => new Store.DeviceDto(d.Id, Text(d, "ownerUid"), Text(d, "name"), Text(d, "secretHash"), d.GetValue<bool>("active"), d.GetValue<bool>("revoked"), [.. d.GetValue<List<string>>("pcIds")],
                d.ContainsField("createdAtUnixMs") ? d.GetValue<long>("createdAtUnixMs") : null, d.ContainsField("secretIssuedAtUnixMs") ? d.GetValue<long>("secretIssuedAtUnixMs") : null,
                d.ContainsField("lastUsedAtUnixMs") ? d.GetValue<long>("lastUsedAtUnixMs") : null, d.ContainsField("previousSecretHash") ? d.GetValue<string>("previousSecretHash") : null,
                d.ContainsField("previousValidUntilUnixMs") ? d.GetValue<long>("previousValidUntilUnixMs") : null))],
            [.. owners.Where(d => d.GetValue<bool>("suspended")).Select(d => d.Id)],
            [.. owners.Where(d => d.ContainsField("historyRetentionDays")).Select(d => new Store.PrefDto(d.Id, d.GetValue<int>("historyRetentionDays"), d.GetValue<bool>("notifyInvitations"), d.GetValue<bool>("diagnosticsConsent")))]);
        _stored = Documents(snapshot).ToDictionary(e => e.Key, e => e.Value.Fingerprint);
        StartWorker();
        return snapshot;
    }

    private static string Text(DocumentSnapshot d, string field) => d.ContainsField(field) ? d.GetValue<string>(field) : throw new FormatException($"{d.Reference.Path} has no {field}");

    /// <summary>Unreadable documents are copied to quarantine-* collections (never trusted, never silently destroyed) and the live collections are left for the next save to replace.</summary>
    public void QuarantineUnreadable()
    {
        var stamp = _time.GetUtcNow().ToUnixTimeSeconds();
        foreach (var name in new[] { Pcs, Devices, Owners })
        {
            var docs = _db.Collection(name).GetSnapshotAsync().GetAwaiter().GetResult();
            foreach (var chunk in docs.Chunk(400))
            {
                var batch = _db.StartBatch();
                foreach (var d in chunk) batch.Set(_db.Collection($"quarantine-{stamp}-{name}").Document(d.Id), d.ToDictionary());
                batch.CommitAsync().GetAwaiter().GetResult();
            }
        }
        _stored = [];   // so the next save rewrites every live document it knows about and deletes the rest
        StartWorker();
    }

    // ---- single-writer lease -------------------------------------------------------------------------------------------------------------------

    private DocumentReference LeaseDocument => _db.Document($"{Meta}/writer");

    /// <summary>
    /// Takes the lease, or throws. A holder that is still renewing after we have waited out one whole lease is a live second gateway on this database, which must never be
    /// allowed to write. A holder that simply stops renewing (a crashed or killed process) expires, so an ordinary restart waits briefly and then takes over.
    /// </summary>
    private void AcquireWriterLease()
    {
        if (_leaseDuration is not { } duration || _renewer is not null) return;   // once per instance: Load may run again (a Store opened on the same backend)
        var deadline = _time.GetUtcNow() + duration + TimeSpan.FromSeconds(5);
        while (true)
        {
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var remainingMs = _db.RunTransactionAsync(async transaction =>
            {
                var held = await transaction.GetSnapshotAsync(LeaseDocument);
                var holder = held.Exists && held.ContainsField("holder") ? held.GetValue<string>("holder") : "";
                var expires = held.Exists && held.ContainsField("expiresUnixMs") ? held.GetValue<long>("expiresUnixMs") : 0;
                var epoch = held.Exists && held.ContainsField("epoch") ? held.GetValue<long>("epoch") : 0;
                if (holder.Length > 0 && holder != _instanceId && expires > now) return expires - now;
                _leaseEpoch = holder == _instanceId ? epoch : epoch + 1;
                transaction.Set(LeaseDocument, new Dictionary<string, object> { ["holder"] = _instanceId, ["expiresUnixMs"] = now + (long)duration.TotalMilliseconds, ["epoch"] = _leaseEpoch });
                return 0L;
            }).GetAwaiter().GetResult();
            if (remainingMs == 0) break;
            if (_time.GetUtcNow() >= deadline)
                throw new InvalidOperationException("Another OrbitDrive gateway instance is running on this database and keeps renewing its writer lease. Two gateways must not share one database; stop the other one.");
            _log?.LogWarning("The Firestore writer lease is held for another {Seconds} s (a previous instance that did not shut down cleanly, or a second gateway); waiting", remainingMs / 1000);
            Thread.Sleep((int)Math.Min(remainingMs + 500, 5000));
        }
        _renewer = Task.Run(() => RenewLoopAsync(duration, _stop.Token));
    }

    private async Task RenewLoopAsync(TimeSpan duration, CancellationToken stop)
    {
        var every = TimeSpan.FromTicks(duration.Ticks / 3);
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(every, stop); } catch (OperationCanceledException) { return; }
            try
            {
                var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
                var stillOurs = await _db.RunTransactionAsync(async transaction =>
                {
                    var held = await transaction.GetSnapshotAsync(LeaseDocument);
                    if (!held.Exists || held.GetValue<string>("holder") != _instanceId || held.GetValue<long>("epoch") != _leaseEpoch) return false;
                    transaction.Update(LeaseDocument, new Dictionary<string, object> { ["expiresUnixMs"] = now + (long)duration.TotalMilliseconds });
                    return true;
                }, cancellationToken: stop);
                if (!stillOurs) { LoseLease(); return; }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { _log?.LogWarning(e, "Renewing the Firestore writer lease failed; will retry"); }   // a later renewal may still succeed before it expires
        }
    }

    private void LoseLease()
    {
        lock (_gate) { if (_leaseLost) return; _leaseLost = true; _pending = null; }
        _log?.LogCritical("The Firestore writer lease was taken over by another gateway instance. This instance has stopped writing and is shutting down.");
        _onLeaseLost?.Invoke();
    }

    /// <summary>True once another instance has taken the lease over; nothing more is written.</summary>
    public bool LeaseLost { get { lock (_gate) return _leaseLost; } }

    private async Task ReleaseWriterLeaseAsync()
    {
        if (_leaseDuration is null || _leaseEpoch == 0) return;
        try
        {
            await _db.RunTransactionAsync(async transaction =>
            {
                var held = await transaction.GetSnapshotAsync(LeaseDocument);
                if (held.Exists && held.GetValue<string>("holder") == _instanceId) transaction.Update(LeaseDocument, new Dictionary<string, object> { ["expiresUnixMs"] = 0L });
            });
        }
        catch (Exception e) { _log?.LogWarning(e, "Could not give the Firestore writer lease back; it will expire by itself"); }
    }

    // ---- saving --------------------------------------------------------------------------------------------------------------------------------

    public void Save(Store.Snapshot snapshot)
    {
        lock (_gate)
        {
            if (_closed) { _log?.LogError("Control metadata changed after the Firestore backend shut down; the change is NOT stored"); return; }
            if (_leaseLost) { _log?.LogError("Control metadata changed after this gateway lost the Firestore writer lease; the change is NOT stored"); return; }
            _pending = snapshot; _acceptedGeneration++;
        }
        StartWorker();
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void StartWorker()
    {
        lock (_gate) { if (!_closed) _worker ??= Task.Run(() => RunAsync(_stop.Token)); }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var failures = 0;
        while (true)
        {
            try { await _wake.WaitAsync(stop); } catch (OperationCanceledException) { /* drain below */ }
            while (true)
            {
                Store.Snapshot snapshot; long generation;
                lock (_gate)
                {
                    if (_pending is null || _leaseLost) { _pending = null; break; }
                    snapshot = _pending; generation = _acceptedGeneration; _pending = null; _writing = true;
                }
                try
                {
                    await WriteAsync(snapshot);
                    failures = 0;
                    lock (_gate) { _writing = false; _storedGeneration = generation; _lastError = null; }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failures++;
                    lock (_gate) { _writing = false; _lastError = e; _pending ??= snapshot; }   // a newer save, if any, supersedes this one
                    _log?.LogError(e, "Writing control metadata to Firestore failed (attempt {Attempt}); retrying", failures);
                    if (stop.IsCancellationRequested) return;
                    try { await Task.Delay(TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * (1L << Math.Min(failures - 1, 5)), TimeSpan.FromMinutes(1).Ticks)), stop); } catch (OperationCanceledException) { }
                }
            }
            if (stop.IsCancellationRequested) return;
        }
    }

    private async Task WriteAsync(Store.Snapshot snapshot)
    {
        var wanted = Documents(snapshot);
        var operations = new List<(string Path, Dictionary<string, object>? Data)>();
        foreach (var (path, doc) in wanted) if (!_stored.TryGetValue(path, out var held) || held != doc.Fingerprint) operations.Add((path, doc.Data));
        foreach (var path in _stored.Keys) if (!wanted.ContainsKey(path)) operations.Add((path, null));
        foreach (var chunk in operations.Chunk(400))
        {
            var batch = _db.StartBatch();
            foreach (var (path, data) in chunk)
            {
                var reference = _db.Document(path);
                if (data is null) batch.Delete(reference); else batch.Set(reference, data);
            }
            await batch.CommitAsync();
        }
        _stored = wanted.ToDictionary(e => e.Key, e => e.Value.Fingerprint);
        await _db.Document($"{Meta}/schema").SetAsync(new Dictionary<string, object> { ["version"] = 1 });
    }

    private static Dictionary<string, (Dictionary<string, object> Data, string Fingerprint)> Documents(Store.Snapshot snapshot)
    {
        var all = new Dictionary<string, (Dictionary<string, object>, string)>();
        void Add(string path, Dictionary<string, object> data) => all[path] = (data, JsonSerializer.Serialize(data.OrderBy(e => e.Key).ToDictionary(e => e.Key, e => e.Value)));
        foreach (var p in snapshot.Pcs)
        {
            var data = new Dictionary<string, object> { ["spki"] = p.Spki, ["name"] = p.Name, ["agentVersion"] = p.AgentVersion, ["lastSeenUnixMs"] = p.LastSeenUnixMs };
            if (p.OwnerUid is not null) data["ownerUid"] = p.OwnerUid;
            Add($"{Pcs}/{p.PcId}", data);
        }
        foreach (var d in snapshot.Devices)
        {
            var data = new Dictionary<string, object> { ["ownerUid"] = d.OwnerUid, ["name"] = d.Name, ["secretHash"] = d.SecretHash, ["active"] = d.Active, ["revoked"] = d.Revoked, ["pcIds"] = d.PcIds.Order().ToList() };
            if (d.CreatedAtUnixMs is { } created) data["createdAtUnixMs"] = created;
            if (d.SecretIssuedAtUnixMs is { } issued) data["secretIssuedAtUnixMs"] = issued;
            if (d.LastUsedAtUnixMs is { } used) data["lastUsedAtUnixMs"] = used;
            if (d.PreviousSecretHash is not null) { data["previousSecretHash"] = d.PreviousSecretHash; data["previousValidUntilUnixMs"] = d.PreviousValidUntilUnixMs ?? 0L; }
            Add($"{Devices}/{d.Id}", data);
        }
        var owners = new Dictionary<string, Dictionary<string, object>>();
        foreach (var uid in snapshot.Suspended ?? []) owners[uid] = new Dictionary<string, object> { ["suspended"] = true };
        foreach (var pref in snapshot.Prefs ?? [])
        {
            if (!owners.TryGetValue(pref.OwnerUid, out var o)) owners[pref.OwnerUid] = o = new Dictionary<string, object> { ["suspended"] = false };
            o["historyRetentionDays"] = pref.HistoryRetentionDays; o["notifyInvitations"] = pref.NotifyInvitations; o["diagnosticsConsent"] = pref.DiagnosticsConsent;
        }
        foreach (var (uid, data) in owners) Add($"{Owners}/{uid}", data);
        return all;
    }

    /// <summary>Waits until everything accepted by <see cref="Save"/> so far is stored. Throws TimeoutException (with the last Firestore error) if it cannot be.</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            Exception? error;
            lock (_gate) { if (_leaseLost || (_pending is null && !_writing && _storedGeneration == _acceptedGeneration)) return; error = _lastError; }
            if (_time.GetUtcNow() > deadline) throw new TimeoutException("Firestore did not accept the pending control metadata in time.", error);
            await Task.Delay(20);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await FlushAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException e) { _log?.LogError(e, "Shutting down with control metadata not yet stored in Firestore"); }
    }

    /// <summary>Safe to call any number of times, from any number of threads (the DI container disposes every registration that points at this instance): all callers wait for the one shutdown.</summary>
    public ValueTask DisposeAsync() => new(_dispose.Value);

    private async Task ShutDownAsync()
    {
        lock (_gate) _closed = true;                       // from here no new save is accepted; what was accepted is flushed below
        try
        {
            try { await FlushAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException e) { _log?.LogError(e, "Shutting down with control metadata not yet stored in Firestore"); }
            try { _stop.Cancel(); } catch (ObjectDisposedException) { /* already cancelled and disposed */ }
            if (_renewer is not null) { try { await _renewer.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { /* stopping anyway */ } }
            await ReleaseWriterLeaseAsync();
            Task? worker; lock (_gate) worker = _worker;
            if (worker is not null)
                try { await worker.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { _log?.LogError("The Firestore writer did not stop within 5 seconds"); }
                catch (OperationCanceledException) { }
        }
        finally { _stop.Dispose(); }
    }
}
