using OrbitDrive.Core;
using Xunit;

namespace OrbitDrive.Core.Tests;

public sealed class AgentLockTests : IDisposable
{
    private readonly string _dir = Path.Join(Path.GetTempPath(), "orbit-lock-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private FileStream Hold() { Directory.CreateDirectory(_dir); return new FileStream(Path.Join(_dir, AgentLock.FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }

    [Fact] public void A_folder_with_no_agent_is_free_and_a_missing_folder_is_created() { Assert.False(AgentLock.IsHeld(_dir)); Assert.True(Directory.Exists(_dir)); }

    [Fact] public void A_running_agents_lock_is_seen_as_held_and_released_when_it_ends()
    {
        using (Hold()) Assert.True(AgentLock.IsHeld(_dir));
        Assert.False(AgentLock.IsHeld(_dir));
    }

    [Fact] public async Task Waiting_returns_true_as_soon_as_a_stopping_agent_lets_go()
    {
        var held = Hold();
        _ = Task.Run(async () => { await Task.Delay(300); held.Dispose(); });
        Assert.True(await AgentLock.WaitUntilFreeAsync(_dir, TimeSpan.FromSeconds(10)));
    }

    [Fact] public async Task Waiting_gives_up_when_the_lock_is_never_released()
    {
        using var held = Hold();
        Assert.False(await AgentLock.WaitUntilFreeAsync(_dir, TimeSpan.FromMilliseconds(400)));
    }

    [Fact] public void Probing_never_disturbs_a_running_agent()
    {
        using var held = Hold();
        for (var i = 0; i < 20; i++) Assert.True(AgentLock.IsHeld(_dir));
        held.WriteByte(1);                                   // the holder can still use its lock file
    }
}
