using IPop.Application.Settings;
using IPop.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class SettingsTests
{
    [Fact]
    public async Task Get_Missing_ReturnsFallback()
    {
        using var t = new TestDb();
        var s = new DbSettings(t.Db);
        Assert.Equal("07:30", await s.GetAsync(SettingKeys.NotifDigestTime, "07:30"));
    }

    [Fact]
    public async Task Set_Then_Get_RoundTrips_AndUpdatesInPlace()
    {
        using var t = new TestDb();
        var s = new DbSettings(t.Db);
        await s.SetAsync(SettingKeys.HrisNotifyOnFailure, false, "admin");
        await s.SetAsync(SettingKeys.HrisNotifyOnFailure, true, "admin");

        Assert.True(await new DbSettings(t.Verify()).GetAsync(SettingKeys.HrisNotifyOnFailure, false));
        Assert.Equal(1, await t.Verify().AppSettings.CountAsync());
    }

    [Fact]
    public async Task Set_WritesAuditEntry()
    {
        using var t = new TestDb();
        await new DbSettings(t.Db).SetAsync(SettingKeys.NotifDndStart, "21:00", "00012345");

        var log = await t.Verify().AuditLogs.SingleAsync();
        Assert.Equal("settings.changed", log.Action);
        Assert.Equal(SettingKeys.NotifDndStart, log.EntityId);
        Assert.Equal("00012345", log.ActorNik);
        Assert.Contains("21:00", log.DetailJson);
    }

    [Fact]
    public async Task Get_CorruptJson_ReturnsFallback()
    {
        using var t = new TestDb();
        t.Db.AppSettings.Add(new IPop.Core.Entities.AppSetting { Key = "x", ValueJson = "not json" });
        await t.Db.SaveChangesAsync();
        Assert.Equal(5, await new DbSettings(t.Verify()).GetAsync("x", 5));
    }
}
