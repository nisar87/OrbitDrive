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

/// <summary>
/// Holds an exclusive lock on a file in the data directory for as long as the process lives, so a second relay started by mistake on the same
/// directory refuses to start instead of overwriting the first one's files (F-019). The operating system drops the lock when the process ends, however it ends.
/// </summary>
public sealed class DataDirectoryLock : IDisposable
{
    public const string FileName = "relay.lock";
    private FileStream? _stream;

    private DataDirectoryLock(FileStream stream) { _stream = stream; }

    /// <summary>Takes the lock or throws <see cref="InvalidOperationException"/> naming the problem (never the directory contents).</summary>
    public static DataDirectoryLock Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, FileName);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                stream.SetLength(0);
                stream.Write(System.Text.Encoding.ASCII.GetBytes($"pid {Environment.ProcessId}\n"));
                stream.Flush();
            }
            catch (IOException) { /* the lock is what matters; the note is only a courtesy */ }
            return new DataDirectoryLock(stream);
        }
        catch (IOException e)
        {
            throw new InvalidOperationException($"Another OrbitDrive relay is already using the data directory ({FileName} is locked). Stop it first; two relays on one directory would overwrite each other's data.", e);
        }
    }

    public void Dispose() { _stream?.Dispose(); _stream = null; }
}
