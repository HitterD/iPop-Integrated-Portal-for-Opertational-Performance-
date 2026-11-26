using IPop.Api.Endpoints;
using IPop.Api.Notifier;
using Xunit;

namespace IPop.Tests;

/// Review H3: the Notifier log upload is bounded — only the caller's own registered PCs, a few files per person and a
/// capped folder.
public class NotifierDeviceLogTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ipop-notifier-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private static NotifierDeviceReport Pc(string machine) =>
        new("dev-" + Guid.NewGuid().ToString("N")[..12], machine, null, null, "1.6.0", null);

    [Fact]
    public async Task Only_a_registered_pc_of_the_caller_is_saved()
    {
        using var t = new TestDb();
        var me = t.AddUser("NL1");
        var other = t.AddUser("NL2");
        await NotifierEndpoints.RegisterAsync(t.Db, me.Id, Pc("LOGISTIC33"), null, DateTime.UtcNow, default);
        await NotifierEndpoints.RegisterAsync(t.Db, other.Id, Pc("FIN-07"), null, DateTime.UtcNow, default);

        Assert.Equal(204, await NotifierDeviceLogs.SaveAsync(t.Db, me.Id, "logistic33", new StringReader("hello"), dir, default));
        Assert.Equal(404, await NotifierDeviceLogs.SaveAsync(t.Db, me.Id, "a1", new StringReader("x"), dir, default));
        Assert.Equal(404, await NotifierDeviceLogs.SaveAsync(t.Db, me.Id, "FIN-07", new StringReader("x"), dir, default));
        Assert.Equal(404, await NotifierDeviceLogs.SaveAsync(t.Db, me.Id, null, new StringReader("x"), dir, default));

        var file = Assert.Single(Directory.GetFiles(dir));
        Assert.Equal($"LOGISTIC33-{me.Id:N}.log", Path.GetFileName(file));
        Assert.Equal("hello", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_long_body_is_cut_at_the_limit()
    {
        using var t = new TestDb();
        var me = t.AddUser("NL3");
        await NotifierEndpoints.RegisterAsync(t.Db, me.Id, Pc("PC1"), null, DateTime.UtcNow, default);
        Assert.Equal(204, await NotifierDeviceLogs.SaveAsync(t.Db, me.Id, "PC1", new StringReader(new string('x', 200_000)), dir, default));
        Assert.Equal(NotifierDeviceLogs.MaxChars, File.ReadAllText(Assert.Single(Directory.GetFiles(dir))).Length);
    }

    [Fact]
    public void Prune_keeps_the_newest_files_per_person_and_the_file_just_written()
    {
        Directory.CreateDirectory(dir);
        var me = Guid.NewGuid();
        var start = DateTime.UtcNow.AddHours(-1);
        string last = "";
        for (var i = 0; i < NotifierDeviceLogs.MaxFilesPerUser + 3; i++)
        {
            last = Path.Combine(dir, $"PC{i}-{me:N}.log");
            File.WriteAllText(last, "x");
            File.SetLastWriteTimeUtc(last, start.AddMinutes(i));
        }
        var someoneElse = Path.Combine(dir, $"OTHER-{Guid.NewGuid():N}.log");
        File.WriteAllText(someoneElse, "y");

        NotifierDeviceLogs.Prune(dir, me, last);

        var mine = Directory.GetFiles(dir, $"*-{me:N}.log");
        Assert.Equal(NotifierDeviceLogs.MaxFilesPerUser, mine.Length);
        Assert.Contains(last, mine);
        Assert.DoesNotContain(Path.Combine(dir, $"PC0-{me:N}.log"), mine);
        Assert.True(File.Exists(someoneElse), "other people's logs are not touched by the per-person limit");
    }
}
