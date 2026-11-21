using IPop.Infrastructure.Ops;
using Xunit;

namespace IPop.Tests;

public class OpsHealthWorkerTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1_000_000_000;
    private static readonly OpsHealthWorker.DiskLimits Limits = new(WarnPercent: 10, CriticalPercent: 5, CriticalFloorBytes: 10 * Gb);
    private static readonly (string, long, long)[] Roomy = [("C:", 100 * Gb, 500 * Gb)];

    [Fact]
    public void A_fresh_good_backup_and_free_disk_raise_nothing() =>
        Assert.Empty(OpsHealthWorker.Problems(new(true, "ipop.dump", null, Now.AddHours(-1), 1), Now, Roomy, Limits, TimeSpan.FromHours(30)));

    [Theory]
    [InlineData(false, 1, "backup-failed")]
    [InlineData(true, 31, "backup-stale")]
    public void Failed_or_overdue_backups_alert(bool ok, int hoursAgo, string key) =>
        Assert.Equal(key, Assert.Single(OpsHealthWorker.Problems(new(ok, "x", "pg_dump failed", Now.AddHours(-hoursAgo), 1), Now, Roomy, Limits, TimeSpan.FromHours(30))).Key);

    [Fact]
    public void A_missing_status_and_a_full_drive_both_alert()
    {
        var p = OpsHealthWorker.Problems(null, Now, [("C:", 5 * Gb, 500 * Gb)], Limits, TimeSpan.FromHours(30));
        Assert.Equal(["backup-missing", "disk-C:-critical"], p.Select(x => x.Key));
    }

    [Theory]
    [InlineData(60, null)]         // 12 % free: fine
    [InlineData(45, "warning")]    // 9 % free
    [InlineData(24, "critical")]   // 4.8 % free
    public void Disk_alerts_have_two_levels_by_percent_free(long freeGb, string? level)
    {
        var p = OpsHealthWorker.Problems(new(true, "x", null, Now, 1), Now, [("C:", freeGb * Gb, 500 * Gb)], Limits, TimeSpan.FromHours(30));
        if (level is null) Assert.Empty(p);
        else Assert.Equal($"disk-C:-{level}", Assert.Single(p).Key);
    }

    [Fact]
    public void A_small_drive_under_the_floor_is_critical_even_above_five_percent()
    {
        var p = Assert.Single(OpsHealthWorker.Problems(new(true, "x", null, Now, 1), Now, [("D:", 9 * Gb, 120 * Gb)], Limits, TimeSpan.FromHours(30)));
        Assert.Equal(("disk-D:-critical", "high"), (p.Key, p.Priority));
    }

    [Fact]
    public void A_warning_is_normal_priority_and_says_how_full_the_drive_is()
    {
        var p = Assert.Single(OpsHealthWorker.Problems(new(true, "x", null, Now, 1), Now, [("C:", 45 * Gb, 500 * Gb)], Limits, TimeSpan.FromHours(30)));
        Assert.Equal("normal", p.Priority);
        Assert.Equal("Drive C: is getting full", p.Title);
        Assert.Equal("45 GB free of 500 GB (9%) on C:.", p.Message);
    }

    [Fact]
    public void Backup_problems_stay_high_priority() =>
        Assert.Equal("high", Assert.Single(OpsHealthWorker.Problems(null, Now, Roomy, Limits, TimeSpan.FromHours(30))).Priority);
    [Fact]
    public void Hris_sync_failing_for_more_than_two_days_alerts_with_the_last_error()
    {
        var p = Assert.Single(OpsHealthWorker.HrisProblems(
            new(LastSuccessAt: Now.AddDays(-11), LastRunAt: Now.AddHours(-8), LastError: "connection refused (10.10.6.51:27080)"),
            Now, TimeSpan.FromHours(48)));
        Assert.Equal(("hris-stale", "high"), (p.Key, p.Priority));
        Assert.Contains("11 days", p.Message);
        Assert.Contains("connection refused", p.Message);
    }

    [Fact]
    public void Hris_sync_that_succeeded_recently_or_never_ran_raises_nothing()
    {
        Assert.Empty(OpsHealthWorker.HrisProblems(new(Now.AddHours(-20), Now.AddHours(-20), null), Now, TimeSpan.FromHours(48)));
        Assert.Empty(OpsHealthWorker.HrisProblems(null, Now, TimeSpan.FromHours(48)));
    }

    [Fact]
    public void Hris_sync_that_never_succeeded_but_keeps_running_alerts()
    {
        var p = Assert.Single(OpsHealthWorker.HrisProblems(new(null, Now.AddHours(-1), "timeout"), Now, TimeSpan.FromHours(48)));
        Assert.Equal("hris-stale", p.Key);
    }
}
