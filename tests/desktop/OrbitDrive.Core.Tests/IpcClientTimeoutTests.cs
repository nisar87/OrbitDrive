using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using OrbitDrive.Core.Ipc;
using Xunit;

namespace OrbitDrive.Core.Tests;

public sealed class IpcClientTimeoutTests
{
    // An agent that has frozen still lets the console connect (the pipe exists) but never reads or answers. Every request must then time out
    // on its own, however many are waiting, instead of hanging for ever (the console used to look connected while nothing ever answered).
    [Fact]
    public async Task Requests_to_an_agent_that_never_reads_time_out_instead_of_hanging()
    {
        var name = "orbit-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        await using var client = await LocalIpcClient.ConnectAsync(name, TimeSpan.FromSeconds(5));
        await accepted;
        client.ReplyTimeout = TimeSpan.FromMilliseconds(700);

        var started = Stopwatch.StartNew();
        var requests = Enumerable.Range(0, 6).Select(_ => Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync("status"))).ToArray();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"all six requests ended within {started.Elapsed}");
    }

    // Large messages to a reader that never reads fill the pipe's buffer: the write itself must also be covered by the time limit.
    [Fact]
    public async Task A_write_that_cannot_complete_also_times_out()
    {
        var name = "orbit-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        await using var client = await LocalIpcClient.ConnectAsync(name, TimeSpan.FromSeconds(5));
        await accepted;
        client.ReplyTimeout = TimeSpan.FromMilliseconds(700);
        var big = new JsonObject { ["blob"] = new string('x', 4 * 1024 * 1024) };
        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync("status", big)).WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_healthy_agent_still_answers_normally()
    {
        var name = "orbit-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        await using var client = await LocalIpcClient.ConnectAsync(name, TimeSpan.FromSeconds(5));
        await accepted;
        var reply = Task.Run(async () =>
        {
            using var reader = new StreamReader(server, leaveOpen: true);
            var line = await reader.ReadLineAsync();
            var id = JsonNode.Parse(line!)!["id"]!.GetValue<string>();
            var answer = new JsonObject { ["id"] = id, ["ok"] = true, ["data"] = new JsonObject { ["state"] = "online" } }.ToJsonString() + "\n";
            await server.WriteAsync(System.Text.Encoding.UTF8.GetBytes(answer));
        });
        var data = await client.SendAsync("status");
        Assert.Equal("online", data!["state"]!.GetValue<string>());
        await reply;
    }
}
