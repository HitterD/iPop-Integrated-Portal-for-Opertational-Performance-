using IPop.Api.Endpoints;
using IPop.Api.Notifier;
using IPop.Core.Entities;
using IPop.Infrastructure.Auth;
using IPop.Notifier.Remote;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// BIGIS #8 phase 1a: one 9-digit remote ID per PC, the unattended password (hash, lockout, revoke) and what the
/// user allowed in the prompt, enforced on the user's PC.
public class RemoteMachineTests
{
    private const string Guid1 = "7f1c2d3e-4b5a-4c6d-8e9f-0a1b2c3d4e5f";
    private static readonly Argon2PasswordHasher Hasher = new();

    private static NotifierDeviceReport Report(string key, string machine, string? guid) =>
        new(key, machine, @"SJA\x", "Windows 11 Pro", "1.8.0", ["10.0.0.1"], guid);

    [Fact]
    public async Task Everyone_on_one_PC_gets_the_same_ID_and_it_stays_stable()
    {
        using var t = new TestDb();
        var a = t.AddUser("RM1"); var b = t.AddUser("RM2");
        var now = DateTime.UtcNow;
        await NotifierEndpoints.RegisterAsync(t.Db, a.Id, Report("dev-aaaaaaaa1", "PC-QC-0024", Guid1), null, now, default);
        await NotifierEndpoints.RegisterAsync(t.Db, b.Id, Report("dev-bbbbbbbb1", "pc-qc-0024", Guid1.ToUpperInvariant()), null, now, default);
        await NotifierEndpoints.RegisterAsync(t.Db, a.Id, Report("dev-aaaaaaaa1", "PC-QC-0024", Guid1), null, now.AddMinutes(1), default);
        await using var v = t.Verify();
        var m = Assert.Single(await v.RemoteMachines.ToListAsync());
        Assert.Matches("^[1-9][0-9]{8}$", m.ConnectId);
        Assert.All(await v.NotifierDevices.ToListAsync(), d => Assert.Equal(m.Id, d.MachineId));
    }

    [Fact]
    public async Task Different_PCs_get_different_IDs_even_with_a_cloned_GUID()
    {
        using var t = new TestDb();
        var a = t.AddUser("RM3");
        var now = DateTime.UtcNow;
        await NotifierEndpoints.RegisterAsync(t.Db, a.Id, Report("dev-cccccccc1", "PC-A", Guid1), null, now, default);
        await NotifierEndpoints.RegisterAsync(t.Db, a.Id, Report("dev-cccccccc2", "PC-B", Guid1), null, now, default);
        await NotifierEndpoints.RegisterAsync(t.Db, a.Id, Report("dev-cccccccc3", "PC-C", null), null, now, default);
        await using var v = t.Verify();
        var ids = await v.RemoteMachines.Select(m => m.ConnectId).ToListAsync();
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public async Task An_older_Notifier_without_GUID_keeps_its_ID_when_the_PC_upgrades()
    {
        using var t = new TestDb();
        var a = t.AddUser("RM4"); var b = t.AddUser("RM5");
        var now = DateTime.UtcNow;
        await NotifierEndpoints.RegisterAsync(t.Db, a.Id, Report("dev-dddddddd1", "PC-OLD", null), null, now, default);
        var before = (await t.Verify().RemoteMachines.SingleAsync()).ConnectId;
        await NotifierEndpoints.RegisterAsync(t.Db, a.Id, Report("dev-dddddddd1", "PC-OLD", Guid1), null, now, default);
        // A second person still on the old Notifier lands on the same PC.
        await NotifierEndpoints.RegisterAsync(t.Db, b.Id, Report("dev-dddddddd2", "PC-OLD", null), null, now, default);
        await using var v = t.Verify();
        var m = Assert.Single(await v.RemoteMachines.ToListAsync());
        Assert.Equal(before, m.ConnectId);
        Assert.StartsWith(Guid1, m.MachineKey);
    }

    [Fact]
    public async Task IDs_are_random_not_sequential()
    {
        using var t = new TestDb();
        var ids = new List<int>();
        for (var i = 0; i < 40; i++)
        {
            var m = await RemoteMachines.EnsureAsync(t.Db, null, "PC-" + i, DateTime.UtcNow, default);
            ids.Add(int.Parse(m!.ConnectId));
        }
        await t.Db.SaveChangesAsync();
        Assert.Equal(40, ids.Distinct().Count());
        var steps = ids.Zip(ids.Skip(1), (x, y) => y - x).ToList();
        Assert.True(steps.Distinct().Count() > 30, "consecutive IDs must not follow a fixed step");
    }

    [Theory]
    [InlineData("482 913 576", "482913576")]
    [InlineData("482-913-576", "482913576")]
    [InlineData("482913576", "482913576")]
    [InlineData("48291357", null)]
    [InlineData("PC-482913576", null)]
    public void Remote_ID_text_is_parsed_and_formatted(string text, string? id)
    {
        Assert.Equal(id, RemoteMachines.ParseId(text));
        if (id is not null) Assert.Equal("482 913 576", RemoteMachines.Format(id));
    }

    [Fact]
    public void The_password_is_stored_as_an_Argon2_hash_and_verified()
    {
        var m = Machine("correct horse battery");
        Assert.DoesNotContain("correct horse battery", m.PasswordHash);
        Assert.Equal(RemoteAccess.Check.Ok, RemoteAccess.Verify(m, "correct horse battery", Hasher, DateTime.UtcNow));
        Assert.Equal(RemoteAccess.Check.Wrong, RemoteAccess.Verify(m, "wrong one!!", Hasher, DateTime.UtcNow));
    }

    [Fact]
    public void Five_wrong_passwords_lock_the_PC_for_15_minutes_then_it_opens_again()
    {
        var m = Machine("secret-password-1");
        var now = DateTime.UtcNow;
        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal(RemoteAccess.Check.Wrong, RemoteAccess.Verify(m, "nope", Hasher, now));
            Assert.Equal(5 - i, RemoteAccess.TriesLeft(m));
        }
        Assert.Equal(RemoteAccess.Check.Locked, RemoteAccess.Verify(m, "nope", Hasher, now));
        // Even the right password is refused while locked.
        Assert.Equal(RemoteAccess.Check.Locked, RemoteAccess.Verify(m, "secret-password-1", Hasher, now.AddMinutes(14)));
        Assert.Equal(RemoteAccess.Check.Ok, RemoteAccess.Verify(m, "secret-password-1", Hasher, now.AddMinutes(16)));
        Assert.Null(m.LockedUntil);
        Assert.Equal(0, m.FailedCount);
    }

    [Fact]
    public void A_right_password_resets_the_count()
    {
        var m = Machine("secret-password-2");
        RemoteAccess.Verify(m, "x", Hasher, DateTime.UtcNow);
        RemoteAccess.Verify(m, "y", Hasher, DateTime.UtcNow);
        Assert.Equal(RemoteAccess.Check.Ok, RemoteAccess.Verify(m, "secret-password-2", Hasher, DateTime.UtcNow));
        Assert.Equal(5, RemoteAccess.TriesLeft(m));
    }

    [Fact]
    public void Unattended_needs_the_switch_and_a_password()
    {
        var m = Machine();
        m.Unattended = true;
        Assert.False(m.UnattendedActive);
        Assert.Equal(RemoteAccess.Check.NotSet, RemoteAccess.Verify(m, "anything", Hasher, DateTime.UtcNow));
        m.PasswordHash = Hasher.Hash("long-enough-pw");
        Assert.True(m.UnattendedActive);
        m.Unattended = false;
        Assert.False(m.UnattendedActive);
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("exactly10c", true)]
    public void New_passwords_need_ten_characters(string pw, bool ok) => Assert.Equal(ok, RemoteAccess.CheckNewPassword(pw) is null);

    [Fact]
    public async Task Changing_the_password_revokes_running_unattended_sessions_only()
    {
        using var t = new TestDb();
        var (helper, device, m) = await SeedAsync(t, "RM6");
        var ua = Session(helper, device, unattended: true);
        var asked = Session(helper, device, unattended: false);
        t.Db.RemoteSessions.AddRange(ua, asked);
        await t.Db.SaveChangesAsync();
        var revoked = await RemoteAccess.SetPasswordAsync(t.Db, m, "a-new-password", helper.Id, Hasher, DateTime.UtcNow, default);
        await t.Db.SaveChangesAsync();
        Assert.Equal([ua.Id], revoked.Select(s => s.Id));
        await using var v = t.Verify();
        Assert.Equal("revoked", (await v.RemoteSessions.SingleAsync(s => s.Id == ua.Id)).EndedBy);
        Assert.Equal("active", (await v.RemoteSessions.SingleAsync(s => s.Id == asked.Id)).Status);
        var row = await v.RemoteMachines.SingleAsync();
        Assert.True(row.UnattendedActive);
        Assert.True(Hasher.Verify("a-new-password", row.PasswordHash!));
    }

    [Fact]
    public async Task Connect_checks_the_password_on_the_server_and_logs_every_attempt()
    {
        using var t = new TestDb();
        var (helper, device, m) = await SeedAsync(t, "RM7");
        m.Unattended = true; m.PasswordHash = Hasher.Hash("server-side-pw");
        await t.Db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        RemoteStart.Request Req(string? pw) => new(null, m.ConnectId, "control", pw, false, null);

        var none = await RemoteStart.StartAsync(t.Db, Hasher, helper.Id, Req(null), now, default);
        Assert.Equal(428, none.Status);
        var wrong = await RemoteStart.StartAsync(t.Db, Hasher, helper.Id, Req("guess"), now, default);
        Assert.Equal(403, wrong.Status);
        var ok = await RemoteStart.StartAsync(t.Db, Hasher, helper.Id, Req("server-side-pw"), now, default);
        Assert.Equal(200, ok.Status);
        Assert.Equal("active", ok.Session!.Status);
        Assert.True(ok.Session.Unattended);
        Assert.Equal(RemoteAccess.AllPermissions, ok.Session.Permissions);

        await using var v = t.Verify();
        var log = await v.RemoteSessions.OrderBy(s => s.RequestedAt).Select(s => s.Status).ToListAsync();
        Assert.Contains("denied", log);
        Assert.Contains("active", log);
    }

    [Fact]
    public async Task A_locked_PC_answers_423_and_logs_it()
    {
        using var t = new TestDb();
        var (helper, _, m) = await SeedAsync(t, "RM8");
        m.Unattended = true; m.PasswordHash = Hasher.Hash("server-side-pw");
        await t.Db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        RemoteStart.Result last = null!;
        for (var i = 0; i < 5; i++)
            last = await RemoteStart.StartAsync(t.Db, Hasher, helper.Id, new(null, m.ConnectId, "control", "bad" + i, false, null), now, default);
        Assert.Equal(423, last.Status);
        var after = await RemoteStart.StartAsync(t.Db, Hasher, helper.Id, new(null, m.ConnectId, "control", "server-side-pw", false, null), now.AddMinutes(1), default);
        Assert.Equal(423, after.Status);
        Assert.Equal(2, await t.Verify().RemoteSessions.CountAsync(s => s.Status == "locked"));
    }

    [Fact]
    public async Task Ask_the_user_instead_skips_the_password()
    {
        using var t = new TestDb();
        var (helper, _, m) = await SeedAsync(t, "RM9");
        m.Unattended = true; m.PasswordHash = Hasher.Hash("server-side-pw");
        await t.Db.SaveChangesAsync();
        var r = await RemoteStart.StartAsync(t.Db, Hasher, helper.Id, new(null, m.ConnectId, "control", null, true, null), DateTime.UtcNow, default);
        Assert.Equal(200, r.Status);
        Assert.Equal("asking", r.Session!.Status);
        Assert.False(r.Session.Unattended);
    }

    [Fact]
    public async Task An_offline_PC_says_so_with_its_last_seen_time()
    {
        using var t = new TestDb();
        var (helper, device, m) = await SeedAsync(t, "RM10");
        device.LastSeenAt = DateTime.UtcNow.AddHours(-16);
        await t.Db.SaveChangesAsync();
        var r = await RemoteStart.StartAsync(t.Db, Hasher, helper.Id, new(null, m.ConnectId, "control", null, false, null), DateTime.UtcNow, default);
        Assert.Equal(409, r.Status);
        Assert.Contains("\"offline\"", System.Text.Json.JsonSerializer.Serialize(r.Body));
    }

    [Fact]
    public async Task The_old_unattended_flag_carries_over_but_grants_nothing_without_a_password()
    {
        using var t = new TestDb();
        var u = t.AddUser("RM11");
        var now = DateTime.UtcNow;
        await NotifierEndpoints.RegisterAsync(t.Db, u.Id, Report("dev-eeeeeeee1", "KIOSK-1", null), null, now, default);
        var d = await t.Db.NotifierDevices.SingleAsync();
        d.Unattended = true;
        await t.Db.SaveChangesAsync();
        await NotifierEndpoints.RegisterAsync(t.Db, u.Id, Report("dev-eeeeeeee1", "KIOSK-1", null), null, now, default);
        var m = await t.Verify().RemoteMachines.SingleAsync();
        Assert.True(m.Unattended);
        Assert.False(m.UnattendedActive);
    }

    [Theory]
    [InlineData("control,clipboard,files", "in", "control", true)]
    [InlineData("control,clipboard,files", "in", "view", false)]
    [InlineData("clipboard,files", "in", "control", false)]
    [InlineData("control,files", "clip", "control", false)]
    [InlineData("control,files", "clip-file", "control", false)]
    [InlineData("control,files", "clip-data", "control", false)]
    [InlineData("control,clipboard", "file", "control", false)]
    [InlineData("control,clipboard,files", "file", "control", true)]
    [InlineData("", "ctl", "view", true)]
    [InlineData("", "chat", "view", true)]
    public void The_users_PC_refuses_what_was_not_allowed(string allowed, string topic, string mode, bool accepted) =>
        Assert.Equal(accepted, RemotePermissions.Parse(allowed).Accepts(topic, mode));

    [Fact]
    public void The_helper_cannot_switch_on_what_was_not_allowed()
    {
        var p = RemotePermissions.Parse("files");
        Assert.Equal("view", p.ClampMode("control"));
        Assert.False(p.ClampClipSync(true));
        Assert.Equal("files", p.ToList());
    }

    [Fact]
    public void View_only_sessions_never_carry_control()
    {
        Assert.Equal("clipboard,files", RemoteAccess.Permissions(["control", "clipboard", "files", "bogus"], "view"));
        Assert.Equal("control,files", RemoteAccess.Permissions(["FILES", "control"], "control"));
    }

    private static RemoteMachine Machine(string? password = null) => new()
    {
        Id = Guid.NewGuid(), MachineKey = "|PC", ComputerName = "PC", ConnectId = "482913576",
        Unattended = password is not null, PasswordHash = password is null ? null : Hasher.Hash(password),
    };

    private static async Task<(User Helper, NotifierDevice Device, RemoteMachine Machine)> SeedAsync(TestDb t, string nik)
    {
        var helper = t.AddUser(nik + "H");
        var user = t.AddUser(nik + "U");
        await NotifierEndpoints.RegisterAsync(t.Db, user.Id, Report("dev-" + nik.ToLowerInvariant() + "xxxxxx", "PC-" + nik, Guid1), null, DateTime.UtcNow, default);
        var device = await t.Db.NotifierDevices.SingleAsync(d => d.UserId == user.Id);
        var machine = await t.Db.RemoteMachines.SingleAsync(m => m.Id == device.MachineId);
        return (helper, device, machine);
    }

    private static RemoteSession Session(User helper, NotifierDevice d, bool unattended) => new()
    {
        Id = Guid.NewGuid(), HelperId = helper.Id, TargetUserId = d.UserId, DeviceId = d.Id, Room = "r", Status = "active",
        Unattended = unattended, RequestedAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow, AliveAt = DateTime.UtcNow,
    };
}
