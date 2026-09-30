using IPop.Application.News;
using IPop.Infrastructure.News;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Launch-video announcements (2026-09-30): admin-only upload creates a published announcement
/// post; the newest open popup shows once per targeted user; "popup off" keeps the post; delete
/// removes post, row and files.
public class NewsVideoServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipop-video-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private NewsVideoService Svc(TestDb t)
    {
        var db = t.Verify();
        return new NewsVideoService(db, new NewsService(db), new NewsVideoOptions { LocalRoot = _root });
    }

    private static string TempMp4(int size = 4096)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ipop-test-{Guid.NewGuid():N}.mp4");
        var bytes = new byte[size];
        "\0\0\0\u0018ftypmp42"u8.ToArray().CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static NewsVideoUpload Upload(string title, IReadOnlyList<NewsTargetInput>? targets = null, string? tmp = null) =>
        new(title, "Launch film", tmp ?? TempMp4(), "iPop_Launch.mp4", "video/mp4", 96, null, null, targets);

    [Fact]
    public async Task Upload_publishes_announcement_and_stores_the_file()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("ADM");
        var reader = t.AddUser("R1");

        var res = await Svc(t).UploadAsync(admin.Id, Upload("Meet the new iPop"), default);
        Assert.True(res.Success, res.Error);
        var v = res.Video!;
        Assert.True(v.PopupActive);
        Assert.InRange(v.PopupUntil!.Value, DateTime.UtcNow.AddDays(13.9), DateTime.UtcNow.AddDays(14.1));

        var post = await new NewsService(t.Verify()).GetAsync(v.NewsPostId, reader.Id, default);
        Assert.NotNull(post);
        Assert.Equal("published", post!.Status);
        Assert.Equal("announcement", post.Type);

        var file = await Svc(t).OpenFileAsync(v.Id, poster: false, default);
        Assert.NotNull(file);
        Assert.True(File.Exists(file!.FullPath));
        Assert.StartsWith(Path.GetFullPath(_root), file.FullPath);
        Assert.Equal("iPop_Launch.mp4", file.FileName);
    }

    [Fact]
    public async Task Only_admin_may_upload_and_non_video_is_rejected()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var admin = t.AddAdmin("ADM");

        Assert.Equal("forbidden", (await Svc(t).UploadAsync(hr.Id, Upload("x"), default)).Error);

        var fake = Path.Combine(Path.GetTempPath(), $"ipop-test-{Guid.NewGuid():N}.mp4");
        File.WriteAllText(fake, "not a video at all");
        Assert.Equal("blocked_type", (await Svc(t).UploadAsync(admin.Id, Upload("x", tmp: fake), default)).Error);
        Assert.Equal("blocked_type", (await Svc(t).UploadAsync(admin.Id, Upload("x") with { FileName = "run.exe" }, default)).Error);
        Assert.Equal("title_required", (await Svc(t).UploadAsync(admin.Id, Upload("  "), default)).Error);
        Assert.Empty(await t.Verify().NewsPosts.ToListAsync());
        File.Delete(fake);
    }

    [Fact]
    public async Task Popup_shows_once_per_user_and_only_the_newest()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("ADM");
        var a = t.AddUser("A");
        var b = t.AddUser("B");

        var older = (await Svc(t).UploadAsync(admin.Id, Upload("Old"), default)).Video!;
        await Task.Delay(10);
        var newer = (await Svc(t).UploadAsync(admin.Id, Upload("New"), default)).Video!;

        Assert.Equal(newer.Id, (await Svc(t).GetPendingPopupAsync(a.Id, default))!.Id);
        Assert.True(await Svc(t).TrackAsync(a.Id, newer.Id, "seen", default));
        Assert.True(await Svc(t).TrackAsync(a.Id, newer.Id, "seen", default));

        // Seen the newest: no popup (the older one never resurfaces).
        Assert.Null(await Svc(t).GetPendingPopupAsync(a.Id, default));
        Assert.Equal(newer.Id, (await Svc(t).GetPendingPopupAsync(b.Id, default))!.Id);

        await Svc(t).TrackAsync(b.Id, newer.Id, "played", default);
        await Svc(t).TrackAsync(b.Id, newer.Id, "downloaded", default);
        var stats = (await Svc(t).ListAsync(admin.Id, default)).Single(x => x.Id == newer.Id);
        Assert.Equal((1, 1, 1), (stats.SeenCount, stats.PlayedCount, stats.DownloadedCount));
        Assert.False(await Svc(t).TrackAsync(b.Id, older.Id, "bogus", default));
    }

    [Fact]
    public async Task Popup_respects_news_targets()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("ADM");
        var inside = t.AddUser("IN");
        var outside = t.AddUser("OUT");

        var v = (await Svc(t).UploadAsync(admin.Id, Upload("Targeted", [new NewsTargetInput("user", inside.Nik)]), default)).Video!;

        Assert.NotNull(await Svc(t).GetPendingPopupAsync(inside.Id, default));
        Assert.Null(await Svc(t).GetPendingPopupAsync(outside.Id, default));
        Assert.Null(await Svc(t).GetAsync(v.Id, outside.Id, default));
        Assert.False(await Svc(t).TrackAsync(outside.Id, v.Id, "played", default));
    }

    [Fact]
    public async Task Popup_off_keeps_the_post_and_delete_removes_everything()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("ADM");
        var reader = t.AddUser("R1");
        var v = (await Svc(t).UploadAsync(admin.Id, Upload("Launch"), default)).Video!;

        var off = await Svc(t).SetPopupAsync(admin.Id, v.Id, null, default);
        Assert.True(off.Success, off.Error);
        Assert.False(off.Video!.PopupActive);
        Assert.Null(await Svc(t).GetPendingPopupAsync(reader.Id, default));
        Assert.NotNull(await Svc(t).GetAsync(v.Id, reader.Id, default));
        Assert.Equal("popup_until_in_past", (await Svc(t).SetPopupAsync(admin.Id, v.Id, DateTime.UtcNow.AddDays(-1), default)).Error);

        var path = (await Svc(t).OpenFileAsync(v.Id, false, default))!.FullPath;
        Assert.Equal("forbidden", (await Svc(t).DeleteAsync(reader.Id, v.Id, default)).Error);
        var del = await Svc(t).DeleteAsync(admin.Id, v.Id, default);
        Assert.True(del.Success, del.Error);

        var db = t.Verify();
        Assert.False(await db.NewsVideos.AnyAsync());
        Assert.False(await db.NewsPosts.AnyAsync(p => p.Id == v.NewsPostId));
        Assert.False(File.Exists(path));
    }
}
