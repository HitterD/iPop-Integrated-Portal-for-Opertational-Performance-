using IPop.Infrastructure.Files;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

/// Big attachments are downloaded from the NAS once and then served from the local copy (a PDF viewer's range
/// requests used to download the whole file again each time).
public class AttachmentFileCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ipop-attcache-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private AttachmentFileCache Cache() => new(new ConfigurationBuilder()
        .AddInMemoryCollection([new("Attachments:CacheDir", _dir)]).Build());

    [Fact]
    public async Task Parallel_and_later_requests_share_one_download()
    {
        var downloads = 0;
        var cache = Cache();
        Task<Stream> Download(CancellationToken _)
        {
            Interlocked.Increment(ref downloads);
            return Task.FromResult<Stream>(new MemoryStream(Enumerable.Repeat((byte)7, 1000).ToArray()));
        }

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync("mail-abc", Download, default)));
        var again = await cache.GetAsync("mail-abc", Download, default);

        Assert.Equal(1, downloads);
        Assert.All(paths, p => Assert.Equal(again, p));
        Assert.Equal(1000, new FileInfo(again).Length);
    }

    [Fact]
    public async Task A_failed_download_leaves_nothing_behind_and_is_retried()
    {
        var cache = Cache();
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync("mail-x", _ => throw new IOException("nas down"), default));
        Assert.Empty(Directory.GetFiles(_dir));
        var ok = await cache.GetAsync("mail-x", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])), default);
        Assert.Equal(3, new FileInfo(ok).Length);
    }
}
