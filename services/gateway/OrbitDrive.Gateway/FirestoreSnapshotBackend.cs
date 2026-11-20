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

    /// <param name="legacy">The previous file backend: when Firestore holds nothing yet, its content is imported once (so switching backends never drops existing pairings).</param>
    public FirestoreSnapshotBackend(FirestoreDb db, TimeProvider time, ILogger? log = null, TimeSpan? retryDelay = null, ISnapshotBackend? legacy = null)
    {
        _db = db; _time = time; _log = log; _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2); _legacy = legacy;
    }

    /// <summary>Creates the database client. Uses GOOGLE_APPLICATION_CREDENTIALS, or the emulator when FIRESTORE_EMULATOR_HOST is set.</summary>
    public static FirestoreDb Connect(string projectId) =>
        new FirestoreDbBuilder { ProjectId = projectId, EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOrProduction }.Build();

    // ---- loading -------------------------------------------------------------------------------------------------------------------------------

    public Store.Snapshot? Load()
    {
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
            [.. devices.Select(d => new Store.DeviceDto(d.Id, Text(d, "ownerUid"), Text(d, "name"), Text(d, "secretHash"), d.GetValue<bool>("active"), d.GetValue<bool>("revoked"), [.. d.GetValue<List<string>>("pcIds")]))],
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

    // ---- saving --------------------------------------------------------------------------------------------------------------------------------

    public void Save(Store.Snapshot snapshot)
    {
        lock (_gate) { _pending = snapshot; _acceptedGeneration++; }
        StartWorker();
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void StartWorker()
    {
        lock (_gate) _worker ??= Task.Run(() => RunAsync(_stop.Token));
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
                    if (_pending is null) break;
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
            Add($"{Devices}/{d.Id}", new Dictionary<string, object> { ["ownerUid"] = d.OwnerUid, ["name"] = d.Name, ["secretHash"] = d.SecretHash, ["active"] = d.Active, ["revoked"] = d.Revoked, ["pcIds"] = d.PcIds.Order().ToList() });
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
            lock (_gate) { if (_pending is null && !_writing && _storedGeneration == _acceptedGeneration) return; error = _lastError; }
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

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_worker is not null) try { await _worker; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
