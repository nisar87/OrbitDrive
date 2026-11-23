using System.IO.Compression;
using OrbitDrive.Core;

namespace OrbitDrive.Core.Tests;

/// <summary>
/// R26: a REAL archive past 4 GiB (not just forced ZIP64 structures). Too heavy for every push, so it runs only when ORBIT_ZIP64_REAL=1, which the manual
/// `nightly-soak` workflow's large-archive job sets; on an ordinary machine it returns at once.
/// </summary>
public sealed class ZipWriterLargeTests
{
    [Fact]
    public async Task A_real_archive_past_4_GiB_is_written_and_read_back_with_every_byte_counted()
    {
        if (Environment.GetEnvironmentVariable("ORBIT_ZIP64_REAL") != "1") return;
        var dir = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "orbit-zip64-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            const long big = 4L * 1024 * 1024 * 1024 + 123_457;     // just past what a plain ZIP entry can hold
            var source = Path.Join(dir, "big.bin");
            await using (var create = new FileStream(source, FileMode.CreateNew, FileAccess.Write)) { create.SetLength(big); create.Position = 100; create.WriteByte(7); create.Position = big - 1; create.WriteByte(9); }
            var archivePath = Path.Join(dir, "out.zip");
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                var zip = new ZipWriter(output);
                await zip.AddBytesAsync("before.txt", DateTime.UtcNow, "first"u8.ToArray(), CancellationToken.None);
                using (var handle = File.OpenHandle(source))
                    await zip.AddFileAsync("big.bin", DateTime.UtcNow, handle, big, compress: false, null, CancellationToken.None);
                await zip.AddBytesAsync("after.txt", DateTime.UtcNow, "last"u8.ToArray(), CancellationToken.None);   // an entry whose offset is past 4 GiB
                zip.Finish();
            }
            using var archive = ZipFile.OpenRead(archivePath);
            Assert.Equal(["before.txt", "big.bin", "after.txt"], archive.Entries.Select(e => e.FullName).ToArray());
            var bigEntry = archive.GetEntry("big.bin")!;
            Assert.Equal(big, bigEntry.Length);
            long counted = 0; byte first7 = 0, last = 0;
            await using (var stream = bigEntry.Open())
            {
                var buffer = new byte[1 << 20]; int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                {
                    if (counted <= 100 && counted + n > 100) first7 = buffer[100 - counted];
                    counted += n; last = buffer[n - 1];
                }
            }
            Assert.Equal(big, counted);
            Assert.Equal(7, first7); Assert.Equal(9, last);
            using var after = new StreamReader(archive.GetEntry("after.txt")!.Open());
            Assert.Equal("last", await after.ReadToEndAsync());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
