namespace OrbitDrive.Core;

/// <summary>
/// The agent holds an exclusive lock on <c>agent.lock</c> in its state folder for as long as it runs (the OS releases it however the process ends).
/// The console uses the same file to tell "an agent is still running or still shutting down" from "no agent": it never has to guess from a process list.
/// </summary>
public static class AgentLock
{
    public const string FileName = "agent.lock";

    public static bool IsHeld(string stateDirectory)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            using var probe = new FileStream(Path.Join(stateDirectory, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Waits for a stopping agent to let go. True as soon as the lock is free; false if it is still held after <paramref name="timeout"/>.</summary>
    public static async Task<bool> WaitUntilFreeAsync(string stateDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (IsHeld(stateDirectory))
        {
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(100, cancellation).ConfigureAwait(false);
        }
        return true;
    }
}
