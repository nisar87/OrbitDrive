using System.Text.Json;
using System.Text.Json.Nodes;
using OrbitDrive.Agent;
using OrbitDrive.Core.Ipc;
using OrbitDrive.Protocol;

// OrbitDrive agent: per-user, never elevated. The WPF owner console drives it over a same-user local pipe;
// "ctl" and the interactive console commands are the headless equivalents.
// Machine-readable events are written to stdout as lines starting with "@@ ".
static void Emit(string name, object? data)
{
    var line = JsonSerializer.Serialize(new { @event = name, data });
    lock (Console.Out) { Console.Out.WriteLine("@@ " + line); Console.Out.Flush(); }
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int Usage()
{
    Console.Error.WriteLine("""
        orbitdrive-agent run [--gateway <https-url>] [--web-origin <https-url>] [--state <dir>] [--name <pc-name>]
        orbitdrive-agent ctl <status|pair|approve|deny|pause|resume|quit> [--state <dir>]      (talks to the running agent)
        orbitdrive-agent ctl add_root <name> <folder> | remove_root <rootId> | set_root_hidden <rootId> <on|off> | set_limits <KB/s, 0 = none> <files at once> | set_gateway <https-url> [web-origin]
        orbitdrive-agent roots add <name> <folder> | list | remove <rootId> [--state <dir>]   (agent not running)

        While "run" is in the foreground you can also type: pair | approve | deny | pause | resume | reload | quit
        """);
    return 2;
}

if (args.Length == 0) return Usage();
var stateDirectory = Option(args, "--state") ?? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbitDrive", "agent");

if (args[0] == "qr" && args.Length >= 2)
{
    // Diagnostic: prints the QR matrix the console draws for a pairing link (used by the encoder conformance test).
    OrbitDrive.Core.QrCode qr;
    try { qr = OrbitDrive.Core.QrCode.Encode(args[1], args.Length >= 3 && int.TryParse(args[2], out var forcedMask) && forcedMask is >= 0 and <= 7 ? forcedMask : -1); }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
    Console.WriteLine(JsonSerializer.Serialize(new { version = qr.Version, mask = qr.Mask, size = qr.Size, rows = qr.ToRows() }));
    return 0;
}

if (args[0] == "ctl" && args.Length >= 2)
{
    var positional = args.Skip(2).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
    var arguments = (args[1], positional.Length) switch
    {
        ("add_root", 2) => new JsonObject { ["name"] = positional[0], ["path"] = positional[1] },
        ("remove_root", 1) => new JsonObject { ["rootId"] = positional[0] },
        ("set_limits", 2) => new JsonObject { ["maxUploadKBps"] = int.Parse(positional[0]), ["maxConcurrentTransfers"] = int.Parse(positional[1]) },
        ("set_root_hidden", 2) => new JsonObject { ["rootId"] = positional[0], ["showHidden"] = positional[1] is "on" or "true" },
        ("set_sendto", 1) => new JsonObject { ["enabled"] = positional[0] is "on" or "true" },
        ("cancel_invitation", 1) => new JsonObject { ["invitationId"] = positional[0] },
        ("revoke_device", 1) => new JsonObject { ["deviceId"] = positional[0] },
        ("set_gateway", 1 or 2) => new JsonObject { ["gateway"] = positional[0], ["webOrigin"] = positional.Length == 2 ? positional[1] : null },
        ("send_files", >= 2) => new JsonObject { ["receiverId"] = positional[0], ["paths"] = new JsonArray([.. positional.Skip(1).Select(p => (JsonNode)p)]) },
        (_, 0) => new JsonObject(),
        _ => null,
    };
    if (arguments is null) return Usage();
    try
    {
        await using var ipc = await LocalIpcClient.ConnectAsync(LocalIpc.PipeName(stateDirectory), TimeSpan.FromSeconds(3));
        Console.WriteLine((await ipc.SendAsync(args[1], arguments))?.ToJsonString() ?? "ok");
        return 0;
    }
    catch (IpcException e) { Console.Error.WriteLine(e.Message); return 1; }
    catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
    {
        // "Send to" while OrbitDrive is not running: leave the selection as DATA in the per-user spool and start the app. It is sent when the
        // app connects, or discarded after five minutes; it is never sent late and never put on a command line.
        if (args[1] == "send_files" && arguments["paths"] is JsonArray chosen)
        {
            var outbox = Path.Join(stateDirectory, "outbox");
            Directory.CreateDirectory(outbox);
            var spool = Path.Join(outbox, Guid.NewGuid().ToString("N"));
            File.WriteAllText(spool + ".tmp", new JsonObject { ["receiverId"] = arguments["receiverId"]!.DeepClone(), ["paths"] = chosen.DeepClone(), ["requestId"] = Guid.NewGuid().ToString("N") }.ToJsonString());
            File.Move(spool + ".tmp", spool + ".send");
            var desktop = Path.Join(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "OrbitDrive.exe" : "OrbitDrive");
            if (File.Exists(desktop)) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(desktop, "--background") { UseShellExecute = false }); } catch (Exception) { /* the spool waits */ } }
            Console.WriteLine("OrbitDrive was not running. Your selection will be sent when it connects (within five minutes).");
            return 0;
        }
        Console.Error.WriteLine("The OrbitDrive agent is not running for this user.");
        return 1;
    }
}

var state = new AgentState(stateDirectory);
if (args[0] == "roots" && args.Length >= 2)
{
    switch (args[1])
    {
        case "add" when args.Length >= 4:
            var added = state.AddRoot(args[2], args[3]);
            Console.WriteLine($"Shared read-only: {added.Name} ({added.RootId}) -> {added.Path}");
            return 0;
        case "list":
            foreach (var root in state.Roots) Console.WriteLine($"{root.RootId}\t{root.Name}\t{root.Path}");
            return 0;
        case "remove" when args.Length >= 3:
            return state.RemoveRoot(args[2]) ? 0 : 1;
        default: return Usage();
    }
}
if (args[0] != "run") return Usage();

// One agent per user state directory. The lock is released by the OS when the process ends, however it ends.
FileStream instanceLock;
try { instanceLock = new FileStream(Path.Join(state.Directory_, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
catch (IOException)
{
    Console.Error.WriteLine("OrbitDrive agent is already running for this user.");
    return 3;
}
using var instance = instanceLock;

if (Option(args, "--gateway") is { } gatewayOption)
{
    try { state.SetGateway(gatewayOption, Option(args, "--web-origin")); }
    catch (Exception e) when (e is ArgumentException or UriFormatException) { Console.Error.WriteLine(e.Message); return 2; }
}
using var host = new AgentHost(state, Option(args, "--name") ?? Environment.MachineName, Emit);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; host.Quit(); };
var ipcTask = host.RunIpcAsync();
host.StartClient();
host.StartOutbox();
Emit("ready", new { state = host.Status()["state"]!.GetValue<string>(), pipe = LocalIpc.PipeName(state.Directory_) });

// Foreground console commands (development convenience; same operations as the local pipe).
_ = Task.Run(async () =>
{
    while (!host.Shutdown.IsCancellationRequested && await Console.In.ReadLineAsync() is { } line)
    {
        try
        {
            switch (line.Trim().ToLowerInvariant())
            {
                case "pair": await host.PairAsync(host.Shutdown); break;
                case "approve": await host.ConfirmAsync(true, host.Shutdown); break;
                case "deny": await host.ConfirmAsync(false, host.Shutdown); break;
                case "pause": host.SetPaused(true); break;
                case "resume": host.SetPaused(false); break;
                case "reload": state.ReloadRoots(); Emit("roots_reloaded", new { count = state.Roots.Count }); break;
                case "quit": host.Quit(); break;
                case "": break;
                default: Emit("error", new { message = "Unknown command." }); break;
            }
        }
        catch (ProtocolException e) { Emit("error", new { code = e.Code.ToString(), message = e.Message }); }
        catch (IpcException e) { Emit("error", new { message = e.Message }); }
        catch (TimeoutException) { Emit("error", new { message = "The service did not answer in time." }); }
        catch (OperationCanceledException) { }
    }
}, CancellationToken.None);

await host.WaitAsync();
await ipcTask;
Emit("stopped", null);
return 0;
