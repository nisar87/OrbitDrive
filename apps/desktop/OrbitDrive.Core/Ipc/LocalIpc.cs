using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrbitDrive.Core.Ipc;

/// <summary>
/// Local owner-console ↔ agent channel: newline-delimited JSON over a named pipe that the OS restricts to the
/// same user (PipeOptions.CurrentUserOnly). The command set is closed; there is no way to pass a shell command.
/// </summary>
public static class LocalIpc
{
    public const int MaxLineBytes = 64 * 1024;
    public const int MaxClients = 4;
    public static readonly IReadOnlySet<string> Commands = new HashSet<string>
    {
        "status", "add_root", "remove_root", "set_root_hidden", "set_limits", "pair", "approve", "deny", "pause", "resume", "set_gateway", "devices", "revoke_device", "quit",
    };

    /// <summary>One pipe per user state directory, so two Windows users never share an endpoint name.</summary>
    public static string PipeName(string stateDirectory)
    {
        var full = Path.GetFullPath(stateDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        return "orbitdrive-agent-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
    }

    /// <summary>Reads one line, refusing anything longer than <see cref="MaxLineBytes"/>. Null at end of stream.</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellation)
    {
        var buffer = new byte[1];
        using var line = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (read == 0) return line.Length == 0 ? null : throw new IOException("Truncated message.");
            if (buffer[0] == (byte)'\n') return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            if (line.Length >= MaxLineBytes) throw new InvalidDataException("Message too large.");
            line.WriteByte(buffer[0]);
        }
    }
}

public sealed class IpcException(string message) : Exception(message);

/// <summary>Agent side. The handler receives a validated command name and its JSON arguments.</summary>
public sealed class LocalIpcServer(string pipeName, Func<string, JsonObject, CancellationToken, Task<JsonNode?>> handler)
{
    private readonly ConcurrentDictionary<Guid, (Stream Stream, SemaphoreSlim Gate)> _clients = new();

    public async Task RunAsync(CancellationToken cancellation)
    {
        var slots = new SemaphoreSlim(LocalIpc.MaxClients, LocalIpc.MaxClients);
        while (!cancellation.IsCancellationRequested)
        {
            try { await slots.WaitAsync(cancellation).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, LocalIpc.MaxClients, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { slots.Release(); continue; }
            _ = ServeAsync(pipe, cancellation).ContinueWith(_ => slots.Release(), TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellation)
    {
        var id = Guid.NewGuid();
        var gate = new SemaphoreSlim(1, 1);
        _clients[id] = (pipe, gate);
        try
        {
            while (await LocalIpc.ReadLineAsync(pipe, cancellation).ConfigureAwait(false) is { } line)
            {
                var reply = new JsonObject { ["ok"] = false };
                try
                {
                    var request = JsonNode.Parse(line) as JsonObject ?? throw new IpcException("Malformed request.");
                    reply["id"] = request["id"]?.DeepClone();
                    var command = request["cmd"]?.GetValue<string>();
                    if (command is null || !LocalIpc.Commands.Contains(command)) throw new IpcException("Unknown command.");
                    reply["data"] = await handler(command, request["args"] as JsonObject ?? [], cancellation).ConfigureAwait(false);
                    reply["ok"] = true;
                }
                catch (Exception e) when (e is IpcException or JsonException or InvalidOperationException or FormatException)
                { reply["error"] = e is IpcException ? e.Message : "Malformed request."; }
                await WriteAsync(pipe, gate, reply, cancellation).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
        finally { _clients.TryRemove(id, out _); await pipe.DisposeAsync().ConfigureAwait(false); }
    }

    private static async Task WriteAsync(Stream stream, SemaphoreSlim gate, JsonNode message, CancellationToken cancellation)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false); await stream.FlushAsync(cancellation).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>Pushes an event to every connected console. Slow or dead clients are dropped, never queued without bound.</summary>
    public void Broadcast(string name, JsonNode? data)
    {
        foreach (var (stream, gate) in _clients.Values)
        {
            var message = new JsonObject { ["event"] = name, ["data"] = data?.DeepClone() };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _ = WriteAsync(stream, gate, message, timeout.Token).ContinueWith(t => { if (t.IsFaulted || t.IsCanceled) { _ = t.Exception; try { stream.Dispose(); } catch (IOException) { } } }, TaskScheduler.Default);
        }
    }
}

/// <summary>Console side: request/response with correlation plus an event callback.</summary>
public sealed class LocalIpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private int _next;

    public event Action<string, JsonNode?>? EventReceived;
    public event Action? Disconnected;

    private LocalIpcClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public static async Task<LocalIpcClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(timeout, cancellation).ConfigureAwait(false); }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
        var client = new LocalIpcClient(pipe);
        _ = client.ReadLoopAsync();
        return client;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await LocalIpc.ReadLineAsync(_pipe, _closed.Token).ConfigureAwait(false) is { } line)
            {
                if (JsonNode.Parse(line) is not JsonObject message) continue;
                if (message["event"]?.GetValue<string>() is { } name) EventReceived?.Invoke(name, message["data"]);
                else if (message["id"]?.GetValue<string>() is { } id && _pending.TryRemove(id, out var completion)) completion.TrySetResult(message);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        foreach (var pending in _pending.Values) pending.TrySetException(new IpcException("The OrbitDrive agent is not running."));
        Disconnected?.Invoke();
    }

    public async Task<JsonNode?> SendAsync(string command, JsonObject? args = null, CancellationToken cancellation = default)
    {
        var id = Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id, ["cmd"] = command, ["args"] = args ?? [] }.ToJsonString() + "\n");
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { await _pipe.WriteAsync(bytes, cancellation).ConfigureAwait(false); await _pipe.FlushAsync(cancellation).ConfigureAwait(false); }
        catch (IOException) { throw new IpcException("The OrbitDrive agent is not running."); }
        finally { _gate.Release(); }
        var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
        if (reply["ok"]?.GetValue<bool>() != true) throw new IpcException(reply["error"]?.GetValue<string>() ?? "Request failed.");
        return reply["data"];
    }

    public async ValueTask DisposeAsync()
    {
        await _closed.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
