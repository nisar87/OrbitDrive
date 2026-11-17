using System.Security.Cryptography;
using OrbitDrive.Core;
using OrbitDrive.Protocol;

namespace OrbitDrive.Core.Tests;

/// <summary>D6: a file the owner picked in Explorer, outside every shared folder, offered through a sealed one-shot grant.</summary>
public sealed class OneShotGrantTests : IDisposable
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider { public DateTimeOffset Now = start; public override DateTimeOffset GetUtcNow() => Now; }

    private readonly string _dir = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "orbit-oneshot-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly SealedTokens _tokens = new(RandomNumberGenerator.GetBytes(32));
    private readonly Clock _clock = new(DateTimeOffset.UtcNow);
    private readonly FileAccessService _service;

    public OneShotGrantTests() => _service = new FileAccessService(() => [], _tokens, null, _clock);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string File_(string name, string content) { var path = Path.Join(_dir, name); File.WriteAllText(path, content); return path; }
    private static ErrorCode CodeOf(Action action) => Assert.Throws<ProtocolException>(action).Code;

    [Fact]
    public async Task A_picked_file_outside_every_shared_folder_can_be_prepared_and_read_exactly()
    {
        var path = File_("note.txt", "hello phone");
        var offer = _service.IssueOneShot(path, TimeSpan.FromHours(1));
        Assert.Equal(["note.txt", "11"], [offer.Name, offer.Size.ToString()]);
        Assert.DoesNotContain(_dir, offer.Grant);                                       // sealed: the folder is not visible to the relay
        var prepared = await _service.PrepareReadAsync(new PrepareReadRequest { EntryId = offer.Grant }, CancellationToken.None);
        Assert.Equal(["note.txt", "sha256-" + Convert.ToHexStringLower(SHA256.HashData("hello phone"u8))], [prepared.Name, prepared.Revision]);
        using var read = _service.OpenRead(prepared.ReadToken, 6, 5);
        var buffer = new byte[5];
        Assert.Equal(5, RandomAccess.Read(read.Handle, buffer, 6));
        Assert.Equal("phone", System.Text.Encoding.UTF8.GetString(buffer));
        Assert.Empty(_service.ListRoots().Roots);                                       // no shared folder was created
    }

    [Fact]
    public void Folders_relative_and_device_paths_and_missing_files_are_refused_precisely()
    {
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => _service.IssueOneShot(_dir, TimeSpan.FromHours(1))));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => _service.IssueOneShot("relative/a.txt", TimeSpan.FromHours(1))));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => _service.IssueOneShot(Path.Join(_dir, "x.txt") + "\0.txt", TimeSpan.FromHours(1))));
        Assert.Equal(ErrorCode.NotFound, CodeOf(() => _service.IssueOneShot(Path.Join(_dir, "missing.txt"), TimeSpan.FromHours(1))));
        var path = File_("ok.txt", "x");
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => _service.IssueOneShot(path, TimeSpan.Zero)));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => _service.IssueOneShot(path, TimeSpan.FromDays(8))));
    }

    [Fact]
    public async Task The_grant_expires()
    {
        var offer = _service.IssueOneShot(File_("e.txt", "x"), TimeSpan.FromMinutes(5));
        var prepared = await _service.PrepareReadAsync(new PrepareReadRequest { EntryId = offer.Grant }, CancellationToken.None);
        _clock.Now = _clock.Now.AddMinutes(6);
        var thrown = await Assert.ThrowsAsync<ProtocolException>(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = offer.Grant }, CancellationToken.None));
        Assert.Equal(ErrorCode.Expired, thrown.Code);
        Assert.Equal(ErrorCode.Expired, CodeOf(() => _service.OpenRead(prepared.ReadToken, 0, 1)));   // an already prepared read stops too
    }

    [Fact]
    public async Task A_changed_moved_or_replaced_file_is_not_served()
    {
        var path = File_("c.txt", "AAAA");
        var offer = _service.IssueOneShot(path, TimeSpan.FromHours(1));
        var stamp = File.GetLastWriteTimeUtc(path);
        await Task.Delay(100);                                                            // NTFS stamps files with a coarse clock (up to ~16 ms); a rewrite inside the same tick is indistinguishable
        File.WriteAllText(path, "BBBB");                                                  // same size
        File.SetLastWriteTimeUtc(path, stamp);                                            // same modified time
        var thrown = await Assert.ThrowsAsync<ProtocolException>(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = offer.Grant }, CancellationToken.None));
        Assert.Equal(ErrorCode.RevisionChanged, thrown.Code);

        var second = File_("d.txt", "1234");
        var grant = _service.IssueOneShot(second, TimeSpan.FromHours(1));
        File.Delete(second);
        thrown = await Assert.ThrowsAsync<ProtocolException>(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = grant.Grant }, CancellationToken.None));
        Assert.Equal(ErrorCode.NotFound, thrown.Code);
    }

    [Fact]
    public async Task Tokens_are_not_interchangeable_and_forgeries_grant_nothing()
    {
        var offer = _service.IssueOneShot(File_("t.txt", "xyz"), TimeSpan.FromHours(1));
        Assert.Equal(ErrorCode.InvalidRequest, CodeOf(() => _service.OpenRead(offer.Grant, 0, 1)));          // a grant is not a read token
        var prepared = await _service.PrepareReadAsync(new PrepareReadRequest { EntryId = offer.Grant }, CancellationToken.None);
        var thrown = await Assert.ThrowsAsync<ProtocolException>(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = prepared.ReadToken }, CancellationToken.None));
        Assert.Equal(ErrorCode.InvalidRequest, thrown.Code);                                                  // a read token is not a grant
        var other = new SealedTokens(RandomNumberGenerator.GetBytes(32));                                    // another PC's key
        var forged = other.Seal(new TokenBody { Type = "o", RootId = FileAccessService.OneShotRoot, Segments = [Path.Join(_dir, "t.txt")], Size = 3, ExpiresUnixMs = long.MaxValue });
        thrown = await Assert.ThrowsAsync<ProtocolException>(() => _service.PrepareReadAsync(new PrepareReadRequest { EntryId = forged }, CancellationToken.None));
        Assert.Equal(ErrorCode.InvalidRequest, thrown.Code);
    }

    [Fact]
    public void A_symlink_is_not_followed()
    {
        if (OperatingSystem.IsWindows()) return;      // creating links needs a privilege there; covered by the integration suite on Linux and by LinkTests
        var target = File_("real.txt", "real");
        var link = Path.Join(_dir, "link.txt");
        File.CreateSymbolicLink(link, target);
        Assert.Equal(ErrorCode.LinkNotAllowed, CodeOf(() => _service.IssueOneShot(link, TimeSpan.FromHours(1))));
    }
}
