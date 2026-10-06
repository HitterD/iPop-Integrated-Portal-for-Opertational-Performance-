using IPop.Infrastructure.Ops;
using Xunit;

namespace IPop.Tests;

public class OpsHealthWorkerTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);
    private static readonly (string, long)[] Roomy = [("C:", 100_000_000_000)];

    [Fact]
    public void A_fresh_good_backup_and_free_disk_raise_nothing() =>
        Assert.Empty(OpsHealthWorker.Problems(new(true, "ipop.dump", null, Now.AddHours(-1), 1), Now, Roomy, 20_000_000_000, TimeSpan.FromHours(30)));

    [Theory]
    [InlineData(false, 1, "backup-failed")]
    [InlineData(true, 31, "backup-stale")]
    public void Failed_or_overdue_backups_alert(bool ok, int hoursAgo, string key) =>
        Assert.Equal(key, Assert.Single(OpsHealthWorker.Problems(new(ok, "x", "pg_dump failed", Now.AddHours(-hoursAgo), 1), Now, Roomy, 20_000_000_000, TimeSpan.FromHours(30))).Key);

    [Fact]
    public void A_missing_status_and_a_full_drive_both_alert()
    {
        var p = OpsHealthWorker.Problems(null, Now, [("C:", 5_000_000_000)], 20_000_000_000, TimeSpan.FromHours(30));
        Assert.Equal(["backup-missing", "disk-C:"], p.Select(x => x.Key));
    }
}
