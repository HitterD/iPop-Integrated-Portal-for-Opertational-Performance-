using IPop.Api.Endpoints;
using IPop.Api.Notifier;
using IPop.Application.Notifier;
using IPop.Core.Entities;
using IPop.Infrastructure.Auth;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// BIGIS #8 v3: the company policy is enforced on the server (not just the switches), concurrent sessions never cut
/// another ICT off, the hourly limit per person, retention and the asking-sweep, PC limits and Admin change audit.
public class RemotePolicyTests
{
    private const string Guid1 = "7f1c2d3e-4b5a-4c6d-8e9f-0a1b2c3d4e5f";
    private static readonly Argon2PasswordHasher Hasher = new();

    private static async Task<(User Helper, NotifierDevice Device, RemoteMachine Machine)> SeedAsync(TestDb t, string nik)
    {
        var helper = t.AddUser(nik + "H");
        var user = t.AddUser(nik + "U");
        await NotifierEndpoints.RegisterAsync(t.Db, user.Id, new(("dev-" + nik + "xxxxxxxx").ToLowerInvariant(), "PC-" + nik, @"SJA\x", "Windows 11", "1.7.1", ["10.0.0.1"], Guid1), "10.0.0.1", DateTime.UtcNow, default);
        var device = await t.Db.NotifierDevices.SingleAsync(d => d.UserId == user.Id);
        return (helper, device, await t.Db.RemoteMachines.SingleAsync(m => m.Id == device.MachineId));
    }

    private static Task<RemoteStart.Result> Connect(TestDb t, Guid helper, RemoteMachine m, RemotePolicy p, bool join = false, string? pw = null, DateTime? now = null) =>
        RemoteStart.StartAsync(t.Db, Hasher, helper, new(null, m.ConnectId, "control", pw, false, null), now ?? DateTime.UtcNow, default, p, join);

    private static string Code(RemoteStart.Result r) => System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(r.Body)).RootElement.GetProperty("code").GetString()!;

    [Fact]
    public async Task Remote_off_blocks_every_connect()
    {
        using var t = new TestDb();
        var (h, _, m) = await SeedAsync(t, "P1");
        var r = await Connect(t, h.Id, m, new RemotePolicy { RemoteOn = false });
        Assert.Equal(403, r.Status); Assert.Equal("off", Code(r));
    }

    [Fact]
    public async Task A_blocked_person_and_list_mode_are_enforced()
    {
        using var t = new TestDb();
        var (h, _, m) = await SeedAsync(t, "P2");
        Assert.Equal("blocked", Code(await Connect(t, h.Id, m, new RemotePolicy { People = new() { [h.Id] = false } })));
        Assert.Equal("blocked", Code(await Connect(t, h.Id, m, new RemotePolicy { Mode = "list" })));
        Assert.Equal(200, (await Connect(t, h.Id, m, new RemotePolicy { Mode = "list", People = new() { [h.Id] = true } })).Status);
    }

    [Fact]
    public async Task An_excluded_PC_refuses_everyone()
    {
        using var t = new TestDb();
        var (h, _, m) = await SeedAsync(t, "P3");
        m.Excluded = true; await t.Db.SaveChangesAsync();
        Assert.Equal("excluded", Code(await Connect(t, h.Id, m, new RemotePolicy())));
    }

    [Fact]
    public async Task The_hourly_limit_per_person_is_enforced()
    {
        using var t = new TestDb();
        var (h, _, m) = await SeedAsync(t, "P4");
        var p = new RemotePolicy { RatePerHour = 5 };
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            t.Db.RemoteSessions.Add(new RemoteSession { Id = Guid.NewGuid(), HelperId = h.Id, TargetUserId = h.Id, DeviceId = (await t.Db.NotifierDevices.FirstAsync()).Id, Room = "r", Status = "declined", RequestedAt = now.AddMinutes(-10 - i) });
        t.Db.RemoteSessions.Add(new RemoteSession { Id = Guid.NewGuid(), HelperId = h.Id, TargetUserId = h.Id, DeviceId = (await t.Db.NotifierDevices.FirstAsync()).Id, Room = "r", Status = "declined", RequestedAt = now.AddHours(-2) });
        await t.Db.SaveChangesAsync();
        var r = await Connect(t, h.Id, m, p, now: now);
        Assert.Equal(429, r.Status); Assert.Equal("rate", Code(r));
        Assert.Null(await RemotePolicyStore.PreflightAsync(t.Db, p, h.Id, m, m.ComputerName, now.AddMinutes(51), default));
    }

    [Fact]
    public async Task A_busy_PC_is_never_taken_over_and_join_is_view_only()
    {
        using var t = new TestDb();
        var (first, device, m) = await SeedAsync(t, "P5");
        var second = t.AddUser("P5X");
        var now = DateTime.UtcNow;
        t.Db.RemoteSessions.Add(new RemoteSession { Id = Guid.NewGuid(), HelperId = first.Id, TargetUserId = device.UserId, DeviceId = device.Id, Room = "room-1", Status = "active", RequestedAt = now, StartedAt = now, AliveAt = now });
        await t.Db.SaveChangesAsync();
        var busy = await Connect(t, second.Id, m, new RemotePolicy(), now: now);
        Assert.Equal(409, busy.Status); Assert.Equal("busy", Code(busy));
        Assert.Contains(first.Name, System.Text.Json.JsonSerializer.Serialize(busy.Body));
        var join = await Connect(t, second.Id, m, new RemotePolicy(), join: true, now: now);
        Assert.Equal(200, join.Status);
        Assert.Equal("view", join.Session!.Mode);
        Assert.Equal("room-1", join.Session.Room);
        Assert.Equal("asking", join.Session.Status); // the user is still asked
        Assert.Equal(1, await t.Verify().RemoteSessions.CountAsync(s => s.Status == "active")); // the first ICT is untouched
    }

    [Fact]
    public async Task Always_ask_still_needs_the_password_and_then_prompts()
    {
        using var t = new TestDb();
        var (h, _, m) = await SeedAsync(t, "P6");
        m.Unattended = true; m.PasswordHash = Hasher.Hash("a-long-password"); m.AlwaysAsk = true; await t.Db.SaveChangesAsync();
        Assert.Equal(428, (await Connect(t, h.Id, m, new RemotePolicy())).Status);
        var ok = await Connect(t, h.Id, m, new RemotePolicy(), pw: "a-long-password");
        Assert.Equal("asking", ok.Session!.Status);
    }

    [Fact]
    public async Task Unattended_off_company_wide_falls_back_to_the_prompt()
    {
        using var t = new TestDb();
        var (h, _, m) = await SeedAsync(t, "P7");
        m.Unattended = true; m.PasswordHash = Hasher.Hash("a-long-password"); await t.Db.SaveChangesAsync();
        var r = await Connect(t, h.Id, m, new RemotePolicy { AllowUnattended = false });
        Assert.Equal("asking", r.Session!.Status);
        Assert.False(r.Session.Unattended);
    }

    [Fact]
    public async Task Unattended_permissions_stay_within_the_PCs_maximum()
    {
        using var t = new TestDb();
        var (h, _, m) = await SeedAsync(t, "P8");
        m.Unattended = true; m.PasswordHash = Hasher.Hash("a-long-password");
        m.AllowedPermissions = "control,clipboard"; m.UnattendedPermissions = "control,files";
        await t.Db.SaveChangesAsync();
        var r = await Connect(t, h.Id, m, new RemotePolicy(), pw: "a-long-password");
        Assert.Equal("control", r.Session!.Permissions);
    }

    [Fact]
    public async Task Policy_lockout_numbers_apply()
    {
        using var t = new TestDb();
        var (h, _, m) = await SeedAsync(t, "P9");
        m.Unattended = true; m.PasswordHash = Hasher.Hash("a-long-password"); await t.Db.SaveChangesAsync();
        var p = new RemotePolicy { LockAttempts = 3, LockMinutes = 5 };
        var now = DateTime.UtcNow;
        Assert.Equal(403, (await Connect(t, h.Id, m, p, pw: "x", now: now)).Status);
        Assert.Equal(403, (await Connect(t, h.Id, m, p, pw: "y", now: now)).Status);
        var third = await Connect(t, h.Id, m, p, pw: "z", now: now);
        Assert.Equal(423, third.Status);
        Assert.True(third.JustLocked);
        Assert.Equal(now.AddMinutes(5), (await t.Verify().RemoteMachines.SingleAsync()).LockedUntil);
        Assert.False((await Connect(t, h.Id, m, p, pw: "a-long-password", now: now)).JustLocked); // still locked, not "just"
    }

    [Fact]
    public async Task No_prompt_policy_uses_the_company_default_ticks()
    {
        using var t = new TestDb();
        var (h, _, m) = await SeedAsync(t, "P10");
        var r = await Connect(t, h.Id, m, new RemotePolicy { RequirePermission = false });
        Assert.Equal("active", r.Session!.Status);
        Assert.Equal("control,clipboard", r.Session.Permissions);
    }

    [Fact]
    public async Task Unanswered_prompts_expire_and_silent_sessions_end()
    {
        using var t = new TestDb();
        var (h, d, _) = await SeedAsync(t, "S1");
        var now = DateTime.UtcNow;
        t.Db.RemoteSessions.AddRange(
            new RemoteSession { Id = Guid.NewGuid(), HelperId = h.Id, TargetUserId = d.UserId, DeviceId = d.Id, Room = "a", Status = "asking", AskSeconds = 30, RequestedAt = now.AddSeconds(-36) },
            new RemoteSession { Id = Guid.NewGuid(), HelperId = h.Id, TargetUserId = d.UserId, DeviceId = d.Id, Room = "b", Status = "asking", AskSeconds = 60, RequestedAt = now.AddSeconds(-36) },
            new RemoteSession { Id = Guid.NewGuid(), HelperId = h.Id, TargetUserId = d.UserId, DeviceId = d.Id, Room = "c", Status = "active", RequestedAt = now.AddMinutes(-5), StartedAt = now.AddMinutes(-5), AliveAt = now.AddMinutes(-3) });
        await t.Db.SaveChangesAsync();
        Assert.Equal(2, (await RemoteSweepWorker.SweepAsync(t.Db, now, default)).Count);
        var rows = await t.Verify().RemoteSessions.OrderBy(s => s.Room).Select(s => s.Status).ToListAsync();
        Assert.Equal(["expired", "asking", "ended"], rows);
    }

    [Fact]
    public async Task History_older_than_the_retention_is_purged()
    {
        using var t = new TestDb();
        var (h, d, m) = await SeedAsync(t, "S2");
        var now = DateTime.UtcNow;
        t.Db.RemoteSessions.AddRange(
            new RemoteSession { Id = Guid.NewGuid(), HelperId = h.Id, TargetUserId = d.UserId, DeviceId = d.Id, Room = "old", Status = "ended", RequestedAt = now.AddDays(-100) },
            new RemoteSession { Id = Guid.NewGuid(), HelperId = h.Id, TargetUserId = d.UserId, DeviceId = d.Id, Room = "new", Status = "ended", RequestedAt = now.AddDays(-10) });
        RemoteAudit.Add(t.Db, m.Id, h.Id, "old change", now.AddDays(-120));
        await t.Db.SaveChangesAsync();
        Assert.Equal(2, await RemoteSweepWorker.PurgeAsync(t.Db, 90, now, default));
        Assert.Equal(["new"], await t.Verify().RemoteSessions.Select(s => s.Room).ToListAsync());
    }

    [Fact]
    public void Policy_values_snap_to_the_offered_options()
    {
        var p = new RemotePolicy { AskSeconds = 31, MaxFileMB = 999999, RatePerHour = 0, RetentionDays = 1, Mode = "x", Quality = "4k" }.Normalize();
        Assert.Equal(30, p.AskSeconds); Assert.Equal(5120, p.MaxFileMB); Assert.Equal(5, p.RatePerHour); Assert.Equal(90, p.RetentionDays);
        Assert.Equal("role", p.Mode); Assert.Equal("auto", p.Quality);
        var d = new RemotePolicy();
        Assert.Equal((30, 10, 5, 15, 10, 2048, 6, 60, 365), (d.AskSeconds, d.MinPassword, d.LockAttempts, d.LockMinutes, d.RatePerHour, d.MaxFileMB, d.ReconnectAttempts, d.ReconnectSeconds, d.RetentionDays));
        Assert.Equal(new RemotePerms(true, true, false), d.DefaultPerms);
    }

    [Fact]
    public void Admin_changes_produce_audit_lines_only_when_something_changed()
    {
        var m = new RemoteMachine { Id = Guid.NewGuid(), MachineKey = "|PC", ComputerName = "PC", ConnectId = "123456789" };
        var c = RemoteAdminEndpoints.Apply(m, new(Alias: "Lobby kiosk", Group: "GA", AlwaysAsk: true, Excluded: true, AllowedPermissions: "control"));
        Assert.Equal(5, c.Count);
        Assert.Equal("Lobby kiosk", m.Alias);
        Assert.Equal("control", m.AllowedPermissions);
        Assert.Empty(RemoteAdminEndpoints.Apply(m, new(Alias: "Lobby kiosk", AlwaysAsk: true)));
    }

    [Fact]
    public async Task ICT_history_shows_only_their_own_sessions()
    {
        using var t = new TestDb();
        var (h, d, m) = await SeedAsync(t, "S3");
        var other = t.AddUser("S3O");
        var now = DateTime.UtcNow;
        t.Db.RemoteSessions.AddRange(
            new RemoteSession { Id = Guid.NewGuid(), HelperId = h.Id, TargetUserId = d.UserId, DeviceId = d.Id, Room = "a", Status = "ended", EndedBy = "helper", RequestedAt = now, StartedAt = now, EndedAt = now.AddMinutes(5) },
            new RemoteSession { Id = Guid.NewGuid(), HelperId = other.Id, TargetUserId = d.UserId, DeviceId = d.Id, Room = "b", Status = "declined", RequestedAt = now });
        RemoteAudit.Add(t.Db, m.Id, other.Id, "Unattended password changed", now);
        await t.Db.SaveChangesAsync();
        var mine = await RemoteHistory.ListAsync(t.Db, h.Id, false, null, null, default);
        Assert.Equal("done", Assert.Single(mine).Result);
        var all = await RemoteHistory.ListAsync(t.Db, null, true, null, null, default);
        Assert.Equal(3, all.Count);
        Assert.Contains(all, r => r.Result == "cfg");
        Assert.Contains("\"Done\"", System.Text.Encoding.UTF8.GetString(RemoteHistory.Csv(mine)));
    }

    [Fact]
    public async Task Rejoin_rechecks_the_policy()
    {
        using var t = new TestDb();
        var (h, d, m) = await SeedAsync(t, "J1");
        var now = DateTime.UtcNow;
        var s = new RemoteSession { Id = Guid.NewGuid(), HelperId = h.Id, TargetUserId = d.UserId, DeviceId = d.Id, Room = "r", Status = "active", RequestedAt = now, StartedAt = now, AliveAt = now };
        t.Db.RemoteSessions.Add(s);
        await t.Db.SaveChangesAsync();
        // The helper isn't ICT in this test DB: give them Admin so only the policy decides.
        Assert.Null(await RemoteRevocation.StillAllowedAsync(t.Db, s, new RemotePolicy(), true, default));
        Assert.Equal("off", await RemoteRevocation.StillAllowedAsync(t.Db, s, new RemotePolicy { RemoteOn = false }, true, default));
        Assert.Equal("blocked", await RemoteRevocation.StillAllowedAsync(t.Db, s, new RemotePolicy { People = new() { [h.Id] = false } }, true, default));
        Assert.Equal("blocked", await RemoteRevocation.StillAllowedAsync(t.Db, s, new RemotePolicy(), false, default)); // no ICT/Admin role any more
        t.Db.RemoteHelperBlocks.Add(new RemoteHelperBlock { UserId = h.Id, SetById = h.Id, CreatedAt = now });
        await t.Db.SaveChangesAsync();
        Assert.Equal("blocked", await RemoteRevocation.StillAllowedAsync(t.Db, s, new RemotePolicy(), true, default));
        t.Db.RemoteHelperBlocks.RemoveRange(t.Db.RemoteHelperBlocks);
        m.Excluded = true; await t.Db.SaveChangesAsync();
        Assert.Equal("excluded", await RemoteRevocation.StillAllowedAsync(t.Db, s, new RemotePolicy(), true, default));
    }

    [Fact]
    public async Task Excluding_blocking_or_switching_off_ends_every_running_session_not_only_unattended()
    {
        using var t = new TestDb();
        var (h, d, m) = await SeedAsync(t, "J2");
        var other = t.AddUser("J2O");
        var now = DateTime.UtcNow;
        RemoteSession S(Guid helper, bool ua, string status = "active") => new() { Id = Guid.NewGuid(), HelperId = helper, TargetUserId = d.UserId, DeviceId = d.Id, Room = "r", Status = status, Unattended = ua, RequestedAt = now, StartedAt = now, AliveAt = now };
        t.Db.RemoteSessions.AddRange(S(h.Id, false), S(h.Id, true), S(other.Id, false, "asking"));
        await t.Db.SaveChangesAsync();

        var byPc = await RemoteRevocation.MachineAsync(t.Db, m.Id, now, default);
        Assert.Equal(3, byPc.Count);
        Assert.All(byPc, x => Assert.Equal("revoked", x.EndedBy));
        await t.Db.SaveChangesAsync();

        t.Db.RemoteSessions.AddRange(S(h.Id, false), S(other.Id, false));
        await t.Db.SaveChangesAsync();
        Assert.Single(await RemoteRevocation.HelperAsync(t.Db, h.Id, now, default));
        await t.Db.SaveChangesAsync();
        Assert.Single(await RemoteRevocation.PolicyAsync(t.Db, new RemotePolicy { Mode = "list", People = new() { [h.Id] = true } }, now, default)); // "other" not listed
        await t.Db.SaveChangesAsync();
        t.Db.RemoteSessions.Add(S(h.Id, false));
        await t.Db.SaveChangesAsync();
        Assert.Single(await RemoteRevocation.PolicyAsync(t.Db, new RemotePolicy { RemoteOn = false }, now, default));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "'=HYPERLINK(\"http://x\")")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("	x", "'	x")]
    [InlineData("PC-QC-0024", "PC-QC-0024")]
    public void Csv_cells_that_look_like_formulas_are_text(string v, string expected) => Assert.Equal(expected, RemoteHistory.Cell(v));

    [Fact]
    public void Csv_export_has_no_live_formulas()
    {
        var rows = new[] { new RemoteHistoryRow(DateTime.UtcNow, "=cmd|' /C calc'!A0", null, "+PC", null, 60, "control", "done", "@evil.xlsx", null) };
        var csv = System.Text.Encoding.UTF8.GetString(RemoteHistory.Csv(rows));
        Assert.DoesNotContain(",\"=", csv); Assert.DoesNotContain(",\"+", csv); Assert.DoesNotContain(",\"@", csv);
        Assert.Contains("\"'=cmd", csv);
    }
}
