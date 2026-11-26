using IPop.Notifier.Remote;
using Xunit;

namespace IPop.Tests;

/// BIGIS #8 v3 on the Notifier: files stream to disk in batches (never whole in memory), the size limit holds,
/// cancelled / cut-off transfers leave nothing behind; reconnect timing and the helper-leave grace.
public sealed class RemoteStreamingTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ipop-stream-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private IncomingFiles New(long max = 1024) => new(() => dir, max);

    [Fact]
    public void Chunks_are_appended_and_the_file_appears_only_when_complete()
    {
        var f = New();
        Assert.NotNull(f.Begin("x1", "report.xlsx", 300));
        Assert.True(f.Append("x1", new byte[100]));
        Assert.True(f.Append("x1", new byte[200]));
        Assert.False(File.Exists(Path.Combine(dir, "report.xlsx")));
        var path = f.End("x1", true);
        Assert.Equal(Path.Combine(dir, "report.xlsx"), path);
        Assert.Equal(300, new FileInfo(path!).Length);
        Assert.Empty(Directory.GetFiles(dir, "*.part"));
    }

    [Fact]
    public void A_file_announced_over_the_limit_is_refused_before_anything_is_written()
    {
        var f = New(1000);
        Assert.Null(f.Begin("x2", "big.iso", 1001));
        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0);
    }

    [Fact]
    public void A_stream_that_grows_past_the_limit_is_cut_and_deleted()
    {
        var f = New(1000);
        f.Begin("x3", "liar.bin", 0); // size unknown
        Assert.True(f.Append("x3", new byte[900]));
        Assert.False(f.Append("x3", new byte[200]));
        Assert.Null(f.End("x3", true));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void More_bytes_than_announced_are_refused()
    {
        var f = New();
        f.Begin("x4", "a.txt", 10);
        Assert.False(f.Append("x4", new byte[11]));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void Cancelled_or_short_transfers_leave_no_file()
    {
        var f = New();
        f.Begin("x5", "a.txt", 100); f.Append("x5", new byte[40]);
        Assert.Null(f.End("x5", false));
        f.Begin("x6", "b.txt", 100); f.Append("x6", new byte[40]);
        Assert.Null(f.End("x6", true)); // said ok, but 40 of 100
        f.Begin("x7", "c.txt", 100);
        f.AbortAll();
        Assert.Equal(0, f.Open);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void Names_are_cleaned_and_never_overwrite()
    {
        var f = New();
        f.Begin("a", @"..\..\evil:name?.txt", 1); f.Append("a", new byte[1]);
        var p1 = f.End("a", true)!;
        f.Begin("b", "evil_name_.txt", 1); f.Append("b", new byte[1]);
        var p2 = f.End("b", true)!;
        Assert.Equal(dir, Path.GetDirectoryName(p1));
        Assert.Equal("evil_name_.txt", Path.GetFileName(p1));
        Assert.Equal("evil_name_ (2).txt", Path.GetFileName(p2));
    }

    [Fact]
    public void Unknown_or_reused_ids_are_refused()
    {
        var f = New();
        Assert.False(f.Append("nope", new byte[1]));
        Assert.NotNull(f.Begin("dup", "a", 1));
        Assert.Null(f.Begin("dup", "b", 1));
        f.AbortAll();
    }

    [Fact]
    public void Rejoin_spreads_the_tries_over_the_window_then_gives_up()
    {
        var r = new RemoteRejoin(6, 60);
        Assert.Equal(TimeSpan.Zero, r.DueAt(1));
        Assert.Equal(TimeSpan.FromSeconds(10), r.DueAt(2));
        Assert.Equal(TimeSpan.FromSeconds(50), r.DueAt(6));
        Assert.False(r.GiveUp(3, TimeSpan.FromSeconds(30)));
        Assert.False(r.GiveUp(6, TimeSpan.FromSeconds(59)));
        Assert.True(r.GiveUp(6, TimeSpan.FromSeconds(60)));
        Assert.True(r.GiveUp(2, TimeSpan.FromSeconds(66))); // whatever happened, the window is over
    }

    [Fact]
    public void A_helper_who_comes_back_cancels_the_leave()
    {
        var t = new HelperLeaveTimer(HelperLeaveTimer.Default);
        Assert.Equal(TimeSpan.FromSeconds(30), t.Grace);
        var first = t.Left();
        t.Back();
        Assert.False(t.ShouldEnd(first));
        var second = t.Left();
        Assert.True(t.ShouldEnd(second));
    }
}
