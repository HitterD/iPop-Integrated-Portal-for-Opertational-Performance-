using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// iPop Notifier PCs: the app reports its computer name / IP / Windows user; only ICT/IT or Admin may list them.
public class NotifierDeviceTests
{
    private static NotifierDeviceReport Report(string key = "dev-0123456789", string machine = "LOGISTIC33") =>
        new(key, machine, @"SJA\budi.santoso", "Windows 11 Pro 24H2", "1.0.0", ["10.12.4.33", "not-an-ip", "fe80::1"]);

    [Fact]
    public async Task Register_upserts_one_row_per_user_and_device_key()
    {
        using var t = new TestDb();
        var u = t.AddUser("ND1");
        var now = DateTime.UtcNow;
        Assert.Equal(204, await NotifierEndpoints.RegisterAsync(t.Db, u.Id, Report(), "10.12.4.33", now, default));
        Assert.Equal(204, await NotifierEndpoints.RegisterAsync(t.Db, u.Id, Report(machine: "LOGISTIC33-NEW"), "10.12.4.34", now.AddMinutes(1), default));
        await using var v = t.Verify();
        var row = Assert.Single(await v.NotifierDevices.ToListAsync());
        Assert.Equal("LOGISTIC33-NEW", row.MachineName);
        Assert.Equal("10.12.4.34", row.RemoteIp);
        Assert.Equal("10.12.4.33, fe80::1", row.LocalIps); // garbage dropped
        Assert.Equal(now, row.FirstSeenAt);
    }

    [Theory]
    [InlineData("short", "PC1")]
    [InlineData("bad key with spaces!!", "PC1")]
    [InlineData("dev-0123456789", "")]
    public async Task Bad_reports_are_rejected(string key, string machine)
    {
        using var t = new TestDb();
        var u = t.AddUser("ND2");
        Assert.Equal(400, await NotifierEndpoints.RegisterAsync(t.Db, u.Id, Report(key, machine), null, DateTime.UtcNow, default));
    }

    [Fact]
    public async Task Ict_tools_are_for_admins_and_ict_departments_including_sub_units()
    {
        using var t = new TestDb();
        var ict = new Department { Id = Guid.NewGuid(), Code = "ICT", Name = "ICT" };
        var sub = new Department { Id = Guid.NewGuid(), Code = "ICT-AIS", Name = "Application & Information System", ParentId = ict.Id };
        var unit = new Department { Id = Guid.NewGuid(), Code = "AIS-DEV", Name = "Developers", ParentId = sub.Id };
        var hr = new Department { Id = Guid.NewGuid(), Code = "HCBP", Name = "Human Capital" };
        t.Db.Departments.AddRange(ict, sub, unit, hr);
        await t.Db.SaveChangesAsync();
        var dev = t.AddUser("ND3"); dev.DepartmentId = unit.Id;
        var staff = t.AddUser("ND4"); staff.DepartmentId = hr.Id;
        var none = t.AddUser("ND5");
        await t.Db.SaveChangesAsync();

        Assert.True(await NotifierEndpoints.IsIctOrAdminAsync(t.Db, dev.Id, isAdmin: false, default));
        Assert.False(await NotifierEndpoints.IsIctOrAdminAsync(t.Db, staff.Id, isAdmin: false, default));
        Assert.False(await NotifierEndpoints.IsIctOrAdminAsync(t.Db, none.Id, isAdmin: false, default));
        Assert.True(await NotifierEndpoints.IsIctOrAdminAsync(t.Db, staff.Id, isAdmin: true, default));
    }

    [Fact]
    public async Task Device_list_shows_online_state_and_owner()
    {
        using var t = new TestDb();
        var u = t.AddUser("ND6");
        var now = DateTime.UtcNow;
        await NotifierEndpoints.RegisterAsync(t.Db, u.Id, Report("dev-aaaaaaaa1", "PC-ONLINE"), "10.0.0.1", now.AddSeconds(-30), default);
        await NotifierEndpoints.RegisterAsync(t.Db, u.Id, Report("dev-aaaaaaaa2", "PC-OLD"), "10.0.0.2", now.AddHours(-2), default);
        var rows = await NotifierEndpoints.ListAsync(t.Verify(), null, now, default);
        Assert.Equal(["PC-ONLINE", "PC-OLD"], rows.Select(r => r.MachineName));
        Assert.True(rows[0].Online);
        Assert.False(rows[1].Online);
        Assert.Equal("User ND6", rows[0].UserName);
    }
}
