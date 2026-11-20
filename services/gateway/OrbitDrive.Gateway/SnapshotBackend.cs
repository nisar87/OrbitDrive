namespace OrbitDrive.Gateway;

/// <summary>Where the gateway's durable control metadata lives (never file bodies). Implementations must be safe to call with the Store's lock held and must never throw for an ordinary write.</summary>
public interface ISnapshotBackend
{
    /// <summary>The last saved state, or null when nothing was saved. May throw JsonException/FormatException for unreadable data.</summary>
    Store.Snapshot? Load();
    void Save(Store.Snapshot snapshot);
    /// <summary>Called after Load found unreadable data: set it aside so it is never trusted and never silently overwritten.</summary>
    void QuarantineUnreadable();
}

/// <summary>One atomically replaced JSON file (owner-only on Linux). The development relay's backend.</summary>
public sealed class FileSnapshotBackend : ISnapshotBackend
{
    private readonly string _file;
    private readonly TimeProvider _time;

    public FileSnapshotBackend(string directory, TimeProvider time)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Join(directory, "registrations.json");
        _time = time;
    }

    public Store.Snapshot? Load() => File.Exists(_file)
        ? System.Text.Json.JsonSerializer.Deserialize<Store.Snapshot>(File.ReadAllText(_file)) ?? throw new System.Text.Json.JsonException("empty")
        : null;

    public void Save(Store.Snapshot snapshot)
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(snapshot));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _file, overwrite: true);
    }

    public void QuarantineUnreadable() => File.Move(_file, _file + ".corrupt-" + _time.GetUtcNow().ToUnixTimeSeconds(), overwrite: true);
}
