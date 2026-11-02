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
    public void App_protocol_detection_can_be_forced_for_QA()
    {
        Environment.SetEnvironmentVariable("IPOP_NOTIFIER_APP_PROTOCOL", "1");
        try { Assert.True(IPopLinks.AppProtocolRegistered()); }
        finally { Environment.SetEnvironmentVariable("IPOP_NOTIFIER_APP_PROTOCOL", "0"); }
        try { Assert.False(IPopLinks.AppProtocolRegistered()); }
        finally { Environment.SetEnvironmentVariable("IPOP_NOTIFIER_APP_PROTOCOL", null); }
    }
}
