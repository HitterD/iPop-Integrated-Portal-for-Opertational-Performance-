using System.Text;
using IPop.Api.Endpoints;
using IPop.Notifier.Core;
using IPop.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace IPop.Tests;

/// Grilled 2026-11-02: videos preview by streaming (ticket + Range) instead of a full download first, and Notifier
/// links reuse the open iPop tab / installed app instead of one more browser tab per message.
public sealed class MediaStreamAndLinksTests
{
    // ── Media tickets ──────────────────────────────────────────────────────────

    [Fact]
    public void Ticket_round_trips_and_rejects_tampering_or_another_purpose()
    {
        var dp = new EphemeralDataProtectionProvider();
        var m = new MediaStreamEndpoints.MediaRef("chat", Guid.NewGuid().ToString("N"), Guid.NewGuid(), "a.mp4", "video/mp4");
        var t = MediaStreamEndpoints.Protect(dp, m);

        Assert.Equal(m, MediaStreamEndpoints.Unprotect(dp, t));
        Assert.Null(MediaStreamEndpoints.Unprotect(dp, t[..^2] + "AA"));
        Assert.Null(MediaStreamEndpoints.Unprotect(dp, null));
        Assert.Null(MediaStreamEndpoints.Unprotect(new EphemeralDataProtectionProvider(), t));
        // A ticket from another feature's protector never opens a media stream.
        var foreign = dp.CreateProtector("IPop.NewsVideo.Stream").ToTimeLimitedDataProtector().Protect("x", TimeSpan.FromHours(1));
        Assert.Null(MediaStreamEndpoints.Unprotect(dp, foreign));
    }

    [Theory]
    [InlineData("video/mp4", true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("application/pdf", false)]
    [InlineData("text/html", false)]
    [InlineData(null, false)]
    public void Only_video_and_audio_stream(string? mime, bool ok) => Assert.Equal(ok, MediaStreamEndpoints.IsStreamable(mime));

    [Fact]
    public async Task Cache_downloads_a_NAS_file_once_even_for_concurrent_viewers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipop-media-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new MediaFileCache(dir);
            var fetches = 0;
            async Task<Stream> Fetch(CancellationToken _)
            {
                Interlocked.Increment(ref fetches);
                await Task.Delay(50);
                return new MemoryStream(Encoding.UTF8.GetBytes("video-bytes"));
            }
            var paths = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => cache.GetAsync("chat-x", Fetch, default)));
            Assert.Equal(1, fetches);
            Assert.All(paths, p => Assert.Equal(paths[0], p));
            Assert.Equal("video-bytes", await File.ReadAllTextAsync(paths[0]!));
            Assert.Empty(Directory.GetFiles(dir, "*.part"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Cache_failed_download_leaves_no_partial_file_and_retries_next_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipop-media-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new MediaFileCache(dir);
            await Assert.ThrowsAsync<IOException>(() => cache.GetAsync("k", _ => throw new IOException("nas down"), default));
            Assert.Empty(Directory.GetFiles(dir));
            var p = await cache.GetAsync("k", _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])), default);
            Assert.Equal(3, new FileInfo(p!).Length);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("a.mp4", true)]
    [InlineData("clip.webm", true)]
    [InlineData("iphone.mov", true)]
    [InlineData("old.avi", false)]
    [InlineData("x.mkv", false)]
    public void Playable_guess(string name, bool playable) => Assert.Equal(playable, MediaStream.LikelyPlayable(name));

    // ── Notifier links ─────────────────────────────────────────────────────────

    private static readonly Uri Web = new("https://ipop.santos.co.id/");

    [Fact]
    public void Notifier_opens_pages_through_the_handoff_page_or_the_app_protocol()
    {
        Assert.Equal("https://ipop.santos.co.id/open.html?to=%2Fchat%2Fr1%3Fm%3D2", IPopLinks.HandoffUrl(Web, "/chat/r1?m=2"));
        Assert.Equal("web+ipop://open?to=%2Fmail", IPopLinks.AppUrl("mail"));
        Assert.Equal("/notifications", IPopLinks.Normalize(null));
        Assert.Equal("/dashboard", IPopLinks.Normalize("//evil.test/x"));
    }

    [Theory]
    [InlineData("https://ipop.santos.co.id/chat/abc", true, "/chat/abc")]
    [InlineData("https://ipop.santos.co.id/mail?mailId=1#r", true, "/mail?mailId=1#r")]
    [InlineData("https://IPOP.santos.co.id/calendar", true, "/calendar")]
    [InlineData("https://ipop.santos.co.id/download/notifier/iPopNotifier-win-Setup.exe", false, "")]
    [InlineData("https://ipop.santos.co.id/api/chat/attachments/1/download", false, "")]
    [InlineData("https://ipop.santos.co.id/notifier/connect?x=1", false, "")]
    [InlineData("https://ipop.santos.co.id/files/report.pdf", false, "")]
    [InlineData("https://idesk.santos.co.id/tickets/1", false, "")]
    [InlineData("https://ipop.santos.co.id:7103/chat", false, "")]
    public void Only_iPop_pages_are_routed_to_the_open_tab(string url, bool page, string path)
    {
        Assert.Equal(page, IPopLinks.IsIPopPage(Web, new Uri(url), out var p));
        Assert.Equal(path, p);
    }

    [Fact]
    public void Handoff_page_itself_is_never_routed_again()
    {
        Assert.True(IPopLinks.IsHandoff(new Uri(IPopLinks.HandoffUrl(Web, "/chat"))));
        Assert.False(IPopLinks.IsHandoff(new Uri("https://ipop.santos.co.id/chat")));
    }

    [Fact]
    public void App_launch_command_from_Chrome_registry_fills_the_url_after_single_argument()
    {
        const string cmd = @"""C:\Users\x\Web Applications\_crx_abc\iPop.exe"" --profile-directory=Default --app-id=abc --single-argument %1";
        var launch = IPopLinks.ParseCommand(cmd, IPopLinks.AppUrl("/chat/r1"));
        Assert.NotNull(launch);
        Assert.Equal(@"C:\Users\x\Web Applications\_crx_abc\iPop.exe", launch.Value.File);
        Assert.Equal("--profile-directory=Default --app-id=abc --single-argument web+ipop://open?to=%2Fchat%2Fr1", launch.Value.Args);
        Assert.Equal((@"C:\b.exe", "\"web+ipop://open?to=%2F\""), IPopLinks.ParseCommand(@"C:\b.exe ""%1""", "web+ipop://open?to=%2F"));
        Assert.Null(IPopLinks.ParseCommand("\"unterminated", "x"));
    }

    [Fact]
    public void App_detection_can_be_forced_off_for_QA()
    {
        Environment.SetEnvironmentVariable("IPOP_NOTIFIER_APP_PROTOCOL", "0");
        try { Assert.Null(IPopLinks.AppCommand()); }
        finally { Environment.SetEnvironmentVariable("IPOP_NOTIFIER_APP_PROTOCOL", null); }
    }

    [Theory]
    [InlineData("(13) Messages — IPop - Google Chrome", true)]
    [InlineData("(13) Messages - IPop - Google Chrome", true)]
    [InlineData("Files - IPop", true)]
    [InlineData("Project Management - iPop", true)]
    [InlineData("(2) ● New in iPop · Mail — IPop", true)]
    [InlineData("Sign in — IPop", false)]
    [InlineData("report.docx - iPop Docs", false)]
    [InlineData("iPop Notifier", false)]
    [InlineData("Dashboard — IPop", true)]
    [InlineData("iPop - (13) Messages — IPop", true)]
    [InlineData("HitterD/iPop-Integrated-Portal - Google Chrome", false)]
    [InlineData("iDesk Enterprise Platform", false)]
    [InlineData("", false)]
    public void Browser_tab_title_recognises_iPop_pages(string title, bool ipop) => Assert.Equal(ipop, IPopLinks.IsIPopTitle(title));

    [Theory]
    [InlineData("● New in iPop · Mail — IPop", true)]
    [InlineData("(4) ● New in iPop · Messages — IPop - Google Chrome", true)]
    [InlineData("(4) Messages — IPop", false)]
    public void Only_the_tab_that_took_the_page_is_marked(string title, bool marked) => Assert.Equal(marked, IPopLinks.IsMarkedTitle(title));

    [Fact]
    public async Task Stream_cache_reports_copying_then_ready_and_remembers_a_NAS_failure_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipop-media-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new MediaFileCache(dir);
            Assert.IsType<CacheState.Missing>(cache.State("a"));
            var gate = new TaskCompletionSource();
            cache.Start("a", async _ => { await gate.Task; return new MemoryStream([1, 2]); });
            Assert.IsType<CacheState.Copying>(cache.State("a"));
            gate.SetResult();
            for (var i = 0; i < 100 && cache.State("a") is not CacheState.Ready; i++) await Task.Delay(20);
            Assert.IsType<CacheState.Ready>(cache.State("a"));

            cache.Start("b", _ => throw new IPop.Application.Files.SynoException("no creds", authFailure: true));
            for (var i = 0; i < 100 && cache.State("b") is not CacheState.Failed; i++) await Task.Delay(20);
            Assert.IsType<CacheState.Failed>(cache.State("b"));
            cache.Forget("b");
            // The failed copy records its error before its finally drops the running-copy entry, so on a
            // busy runner "Copying" can show for a few ms after Forget; it must settle to Missing.
            for (var i = 0; i < 100 && cache.State("b") is CacheState.Copying; i++) await Task.Delay(20);
            Assert.IsType<CacheState.Missing>(cache.State("b"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Immediate_copy_failures_do_not_leave_completed_tasks_marked_as_copying()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipop-media-immediate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new MediaFileCache(dir);
            for (var i = 0; i < 200; i++)
            {
                var key = $"immediate-{i}";
                await Assert.ThrowsAsync<IOException>(() => cache.GetAsync(key,
                    _ => throw new FileNotFoundException(), CancellationToken.None));
                cache.Forget(key);
                Assert.IsType<CacheState.Missing>(cache.State(key));
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("clip.wmv", "video/x-ms-wmv")]
    [InlineData("old.mpg", "video/mpeg")]
    [InlineData("a.mp4", "video/mp4")]
    public void Every_video_kind_Files_offers_has_a_media_type(string name, string mime) => Assert.Equal(mime, MediaStreamEndpoints.MediaMime(name));
}
