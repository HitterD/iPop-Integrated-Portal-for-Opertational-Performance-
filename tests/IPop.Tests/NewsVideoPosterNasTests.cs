using IPop.Application.Files;
using IPop.Application.News;
using IPop.Infrastructure.Attachments;
using IPop.Infrastructure.News;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// The poster is archived next to the video on the NAS and restored from there when the API
/// disk lost it, so a video post never shows a broken thumbnail after a move or a wiped folder.
public class NewsVideoPosterNasTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipop-poster-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeNas : ISynologyGateway
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public Task<string> AuthAsync(string server, string u, string p, CancellationToken ct) => Task.FromResult("sid");
        public Task<IReadOnlyList<string>> ListSharesAsync(string server, string sid, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SynoItem>> ListFolderAsync(string server, string sid, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string server, string sid, string path, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(Files[path]));
        public async Task UploadAsync(string server, string sid, string dir, string fn, Stream c, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await c.CopyToAsync(ms, ct);
            Files[$"{dir}/{fn}"] = ms.ToArray();
        }
        public Task DeleteAsync(string server, string sid, string path, CancellationToken ct) { Files.Remove(path); return Task.CompletedTask; }
        public Task RenameAsync(string server, string sid, string path, string name, CancellationToken ct) => Task.CompletedTask;
        public Task CreateFolderAsync(string server, string sid, string folderPath, string name, CancellationToken ct) => Task.CompletedTask;
        public Task CopyMoveAsync(string server, string sid, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct) => Task.CompletedTask;
        public Task<SynoShareLink> CreateSharingLinkAsync(string server, string sid, string path, string? password, int? expiresInDays, CancellationToken ct)
            => Task.FromResult(new SynoShareLink("id", "url", path, null, false));
        public Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(string server, string sid, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SynoShareLink>>([]);
        public Task DeleteSharingLinkAsync(string server, string sid, string id, CancellationToken ct) => Task.CompletedTask;
        public Task<SynoStorageInfo> GetStorageInfoAsync(string server, string sid, CancellationToken ct)
            => Task.FromResult(new SynoStorageInfo(server, 0, 0, 0));
    }

    private static readonly AttachmentOptions Nas = new() { Server = "spj", Share = "ipop", Username = "svc", Password = "pw", MaxBytes = 1024 };

    private NewsVideoService Svc(TestDb t, FakeNas gw) =>
        new(t.Verify(), new NewsService(t.Verify()), new NewsVideoOptions { LocalRoot = _root }, gw, Nas, new AttachmentSession());

    private static string Temp(string ext, byte[] head, int size = 2048)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ipop-test-{Guid.NewGuid():N}{ext}");
        var bytes = new byte[size];
        head.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Mp4() => Temp(".mp4", "\0\0\0\u0018ftypmp42"u8.ToArray());
    private static string Jpeg() => Temp(".jpg", [0xFF, 0xD8, 0xFF, 0xE0]);

    [Fact]
    public async Task Archive_copies_the_poster_and_a_lost_poster_comes_back_from_the_nas()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("ADM");
        var gw = new FakeNas();
        var up = new NewsVideoUpload("Meet the new iPop", "Launch film", Mp4(), "launch.mp4", "video/mp4", 116, Jpeg(), null, null);
        var res = await Svc(t, gw).UploadAsync(admin.Id, up, default);
        Assert.True(res.Success, res.Error);

        Assert.True(await Svc(t, gw).ArchiveAsync(res.Video!.Id, default));
        var row = await t.Verify().NewsVideos.AsNoTracking().SingleAsync();
        var posterNas = NewsVideoService.PosterNasPath(row);
        Assert.NotNull(posterNas);
        Assert.Contains(posterNas!, gw.Files.Keys);
        Assert.Contains(row.NasPath!, gw.Files.Keys);

        // The local poster disappears (wiped folder, new content root): it is restored on demand.
        var local = Path.Combine(_root, row.ThumbnailPath!);
        File.Delete(local);
        var file = await Svc(t, gw).OpenFileAsync(row.Id, poster: true, default);
        Assert.NotNull(file);
        Assert.True(File.Exists(local));
        Assert.Equal(gw.Files[posterNas!], await File.ReadAllBytesAsync(local));
    }

    [Fact]
    public async Task Startup_backfill_archives_posters_of_videos_archived_before()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("ADM");
        var gw = new FakeNas();
        var up = new NewsVideoUpload("Old launch", "Film", Mp4(), "old.mp4", "video/mp4", 30, Jpeg(), null, null);
        var res = await Svc(t, gw).UploadAsync(admin.Id, up, default);
        await Svc(t, gw).ArchiveAsync(res.Video!.Id, default);
        var row = await t.Verify().NewsVideos.AsNoTracking().SingleAsync();
        // Simulate the old behaviour: only the video was on the NAS.
        gw.Files.Remove(NewsVideoService.PosterNasPath(row)!);

        Assert.Equal(1, await Svc(t, gw).ArchiveMissingPostersAsync(default));
        Assert.Contains(NewsVideoService.PosterNasPath(row)!, gw.Files.Keys);
    }

    [Fact]
    public async Task Deleting_the_video_removes_the_archived_poster_too()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("ADM");
        var gw = new FakeNas();
        var up = new NewsVideoUpload("Temp", "Film", Mp4(), "t.mp4", "video/mp4", 10, Jpeg(), null, null);
        var res = await Svc(t, gw).UploadAsync(admin.Id, up, default);
        await Svc(t, gw).ArchiveAsync(res.Video!.Id, default);
        Assert.Equal(2, gw.Files.Count);

        Assert.True((await Svc(t, gw).DeleteAsync(admin.Id, res.Video.Id, default)).Success);
        Assert.Empty(gw.Files);
    }
}
