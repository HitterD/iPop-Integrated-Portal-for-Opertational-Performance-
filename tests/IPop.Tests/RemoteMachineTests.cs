using IPop.Api.Endpoints;
using IPop.Api.Notifier;
using IPop.Core.Entities;
using IPop.Infrastructure.Auth;
using IPop.Infrastructure.Calls;
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
        // A second person whose Notifier reports the PC's GUID joins it; one on an old Notifier (name only) does not:
        // a name alone is not proof of being on that PC (review PR #5).
        await NotifierEndpoints.RegisterAsync(t.Db, b.Id, Report("dev-dddddddd2", "PC-OLD", Guid1), null, now, default);
        await using var v = t.Verify();
        var m = Assert.Single(await v.RemoteMachines.ToListAsync());
        Assert.Equal(before, m.ConnectId);
        Assert.StartsWith(Guid1, m.MachineKey);
        Assert.All(await v.NotifierDevices.ToListAsync(), d => Assert.Equal(m.Id, d.MachineId));
    }

    [Fact]
    public async Task A_name_only_report_from_someone_else_never_gets_the_PCs_ID()
    {
        using var t = new TestDb();
        var owner = t.AddUser("SP1"); var attacker = t.AddUser("SP2");
        var now = DateTime.UtcNow;
        await NotifierEndpoints.RegisterAsync(t.Db, owner.Id, Report("dev-spoof0001", "PC-FIN-007", Guid1), "10.0.0.7", now, default);
        await NotifierEndpoints.RegisterAsync(t.Db, attacker.Id, Report("dev-spoof0002", "PC-FIN-007", null), "10.0.0.66", now, default);
        await using var v = t.Verify();
        var devices = await v.NotifierDevices.ToListAsync();
        var real = devices.Single(d => d.UserId == owner.Id).MachineId;
        var fake = devices.Single(d => d.UserId == attacker.Id).MachineId;
        Assert.NotEqual(real, fake);
        Assert.Equal(2, await v.RemoteMachines.CountAsync());
    }

    [Fact]
    public async Task A_name_only_PC_with_another_persons_Notifier_is_not_adopted_by_a_GUID_report()
    {
        using var t = new TestDb();
        var owner = t.AddUser("SP3"); var attacker = t.AddUser("SP4");
        var now = DateTime.UtcNow;
        await NotifierEndpoints.RegisterAsync(t.Db, owner.Id, Report("dev-spoof0003", "PC-HR-1", null), null, now, default);
        var id = (await t.Verify().RemoteMachines.SingleAsync()).ConnectId;
        await NotifierEndpoints.RegisterAsync(t.Db, attacker.Id, Report("dev-spoof0004", "PC-HR-1", Guid1), null, now, default);
        await using var v = t.Verify();
        var attackerMachine = await v.RemoteMachines.SingleAsync(m => m.Id == v.NotifierDevices.Single(d => d.UserId == attacker.Id).MachineId);
        Assert.NotEqual(id, attackerMachine.ConnectId);
        Assert.Equal("|PC-HR-1", (await v.RemoteMachines.SingleAsync(m => m.ConnectId == id)).MachineKey);
    }

    [Fact]
    public async Task Connect_refuses_a_PC_reported_from_two_addresses_and_Devices_flags_it()
    {
        using var t = new TestDb();
        var helper = t.AddUser("SP5H"); var a = t.AddUser("SP5A"); var b = t.AddUser("SP5B");
        var now = DateTime.UtcNow;
        await NotifierEndpoints.RegisterAsync(t.Db, a.Id, Report("dev-spoof0005", "PC-QC-1", Guid1), "10.0.0.5", now, default);
        await NotifierEndpoints.RegisterAsync(t.Db, b.Id, Report("dev-spoof0006", "PC-QC-1", Guid1), "10.9.9.9", now, default);
        var m = await t.Verify().RemoteMachines.SingleAsync();
        var r = await RemoteStart.StartAsync(t.Db, Hasher, helper.Id, new(null, m.ConnectId, "control", null, false, null), now, default);
        Assert.Equal(409, r.Status);
        Assert.Contains("\"conflict\"", System.Text.Json.JsonSerializer.Serialize(r.Body));
        Assert.Contains(m.Id, await RemoteMachines.ConflictsAsync(t.Db, now, default));
    }

    [Fact]
    public async Task The_same_name_with_two_GUIDs_is_flagged()
    {
        using var t = new TestDb();
        var a = t.AddUser("SP6A"); var b = t.AddUser("SP6B");
        var now = DateTime.UtcNow;
        await NotifierEndpoints.RegisterAsync(t.Db, a.Id, Report("dev-spoof0007", "PC-X", Guid1), "10.0.0.1", now, default);
        await NotifierEndpoints.RegisterAsync(t.Db, b.Id, Report("dev-spoof0008", "PC-X", Guid.NewGuid().ToString()), "10.0.0.2", now, default);
        Assert.Equal(2, (await RemoteMachines.ConflictsAsync(t.Db, now, default)).Count);
    }

    [Fact]
    public async Task Two_Notifiers_of_one_PC_registering_at_once_end_on_one_machine()
    {
        using var t = new TestDb();
        var a = t.AddUser("RC1"); var b = t.AddUser("RC2");
        var now = DateTime.UtcNow;
        await NotifierEndpoints.RegisterAsync(t.Db, a.Id, Report("dev-race00001", "PC-OTHER", null), null, now, default);
        // b's Notifier decides "no machine yet"; just before it saves, a's Notifier on the same PC saves that machine.
        var race = new RaceFirst(t.Options, a.Id);
        var opts = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<IPop.Infrastructure.Persistence.IPopDbContext>(t.Options).AddInterceptors(race).Options;
        await using var dbB = new IPop.Infrastructure.Persistence.IPopDbContext(opts);
        Assert.Equal(204, await NotifierEndpoints.RegisterAsync(dbB, b.Id, Report("dev-race00002", "PC-RACE", Guid1), null, now, default));
        Assert.True(race.Fired);
        await using var v = t.Verify();
        var m = Assert.Single(await v.RemoteMachines.Where(x => x.ComputerName == "PC-RACE").ToListAsync());
        Assert.Equal("111222333", m.ConnectId); // the machine that won, not a second one
        Assert.All(await v.NotifierDevices.ToListAsync(), d => Assert.Equal(m.Id, d.MachineId));
    }

    [Fact]
    public async Task Revoking_drops_both_sides_from_the_LiveKit_room()
    {
        using var t = new TestDb();
        var (helper, device, m) = await SeedAsync(t, "LK1");
        var s = Session(helper, device, unattended: true);
        t.Db.RemoteSessions.Add(s);
        await t.Db.SaveChangesAsync();
        var revoked = await RemoteAccess.RemoveAsync(t.Db, m, helper.Id, DateTime.UtcNow, default);
        await t.Db.SaveChangesAsync();
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        var hub = new NullHub();
        await RemoteMachineEndpoints.TellRevokedAsync(hub, lk, null, revoked, default);
        Assert.Equal(["host:" + device.UserId, "helper:" + helper.Id], lk.Removed.Where(r => r.Room == s.Room).Select(r => r.Identity));
        Assert.Equal(1, hub.Sent); // one "remoteEnded" to both people
    }

    [Fact]
    public void Join_tokens_are_short_lived() => Assert.True(RemoteEndpoints.TokenLifetime <= TimeSpan.FromMinutes(10));

    /// Counts hub sends; nothing is delivered.
    private sealed class NullHub : Microsoft.AspNetCore.SignalR.IHubContext<IPop.Api.Notifications.NotificationHub>
    {
        private readonly NullClients clients = new();
        public int Sent => clients.Sent;
        public Microsoft.AspNetCore.SignalR.IHubClients Clients => clients;
        public Microsoft.AspNetCore.SignalR.IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class NullClients : Microsoft.AspNetCore.SignalR.IHubClients, Microsoft.AspNetCore.SignalR.IClientProxy
    {
        public int Sent;
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) { Interlocked.Increment(ref Sent); return Task.CompletedTask; }
        public Microsoft.AspNetCore.SignalR.IClientProxy All => this;
        public Microsoft.AspNetCore.SignalR.IClientProxy AllExcept(IReadOnlyList<string> excluded) => this;
        public Microsoft.AspNetCore.SignalR.IClientProxy Client(string connectionId) => this;
        public Microsoft.AspNetCore.SignalR.IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;
        public Microsoft.AspNetCore.SignalR.IClientProxy Group(string groupName) => this;
        public Microsoft.AspNetCore.SignalR.IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => this;
        public Microsoft.AspNetCore.SignalR.IClientProxy Groups(IReadOnlyList<string> groupNames) => this;
        public Microsoft.AspNetCore.SignalR.IClientProxy User(string userId) => this;
        public Microsoft.AspNetCore.SignalR.IClientProxy Users(IReadOnlyList<string> userIds) => this;
    }

    /// Before the first save that adds a machine, another request saves the same PC (as a concurrent sign-in would).
    private sealed class RaceFirst(Microsoft.EntityFrameworkCore.DbContextOptions<IPop.Infrastructure.Persistence.IPopDbContext> options, Guid otherUser)
        : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Fired { get; private set; }
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData e, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        {
            var added = e.Context!.ChangeTracker.Entries<RemoteMachine>().FirstOrDefault(x => x.State == Microsoft.EntityFrameworkCore.EntityState.Added);
            if (!Fired && added is not null)
            {
                Fired = true;
                await using var other = new IPop.Infrastructure.Persistence.IPopDbContext(options);
                var m = new RemoteMachine { Id = Guid.NewGuid(), MachineKey = added.Entity.MachineKey, ComputerName = added.Entity.ComputerName, ConnectId = "111222333", CreatedAt = DateTime.UtcNow };
                other.RemoteMachines.Add(m);
                await other.SaveChangesAsync(ct);
                var dev = await other.NotifierDevices.FirstAsync(d => d.UserId == otherUser, ct);
                dev.MachineName = m.ComputerName; dev.MachineId = m.Id;
                await other.SaveChangesAsync(ct);
            }
            return result;
        }
    }

    [Fact]
    public async Task IDs_are_random_not_sequential()
    {
        using var t = new TestDb();
        var ids = new List<int>();
        for (var i = 0; i < 40; i++)
        {
            var m = await RemoteMachines.EnsureAsync(t.Db, Guid.NewGuid(), null, "PC-" + i, DateTime.UtcNow, default);
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
    public async Task The_password_is_stored_as_an_Argon2_hash_and_verified()
    {
        using var t = new TestDb();
        var m = await StoredAsync(t, "correct horse battery");
        Assert.DoesNotContain("correct horse battery", m.PasswordHash);
        Assert.Equal(RemoteAccess.Check.Ok, (await RemoteAccess.VerifyAsync(t.Db, m.Id, "correct horse battery", Hasher, DateTime.UtcNow, default)).Check);
        Assert.Equal(RemoteAccess.Check.Wrong, (await RemoteAccess.VerifyAsync(t.Db, m.Id, "wrong one!!", Hasher, DateTime.UtcNow, default)).Check);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_PC_for_15_minutes_then_it_opens_again()
    {
        using var t = new TestDb();
        var m = await StoredAsync(t, "secret-password-1");
        var now = DateTime.UtcNow;
        for (var i = 1; i <= 4; i++)
        {
            var r = await RemoteAccess.VerifyAsync(t.Db, m.Id, "nope", Hasher, now, default);
            Assert.Equal(RemoteAccess.Check.Wrong, r.Check);
            Assert.Equal(5 - i, r.TriesLeft);
        }
        Assert.Equal(RemoteAccess.Check.Locked, (await RemoteAccess.VerifyAsync(t.Db, m.Id, "nope", Hasher, now, default)).Check);
        // Even the right password is refused while locked.
        Assert.Equal(RemoteAccess.Check.Locked, (await RemoteAccess.VerifyAsync(t.Db, m.Id, "secret-password-1", Hasher, now.AddMinutes(14), default)).Check);
        Assert.Equal(RemoteAccess.Check.Ok, (await RemoteAccess.VerifyAsync(t.Db, m.Id, "secret-password-1", Hasher, now.AddMinutes(16), default)).Check);
        var row = await t.Verify().RemoteMachines.SingleAsync();
        Assert.Null(row.LockedUntil);
        Assert.Equal(0, row.FailedCount);
    }

    [Fact]
    public async Task A_right_password_resets_the_count()
    {
        using var t = new TestDb();
        var m = await StoredAsync(t, "secret-password-2");
        await RemoteAccess.VerifyAsync(t.Db, m.Id, "x", Hasher, DateTime.UtcNow, default);
        await RemoteAccess.VerifyAsync(t.Db, m.Id, "y", Hasher, DateTime.UtcNow, default);
        var ok = await RemoteAccess.VerifyAsync(t.Db, m.Id, "secret-password-2", Hasher, DateTime.UtcNow, default);
        Assert.Equal(RemoteAccess.Check.Ok, ok.Check);
        Assert.Equal(0, (await t.Verify().RemoteMachines.SingleAsync()).FailedCount);
    }

    [Fact]
    public async Task Parallel_wrong_passwords_each_count_and_lock_exactly_once()
    {
        // Ten guesses at once from separate requests (own DbContext each): a read-increment-save would lose counts.
        using var t = new TestDb();
        var m = await StoredAsync(t, "secret-password-3");
        var now = DateTime.UtcNow;
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            await using var db = new IPop.Infrastructure.Persistence.IPopDbContext(t.Options);
            return await RemoteAccess.VerifyAsync(db, m.Id, "guess" + i, Hasher, now, default);
        }));
        Assert.Equal(4, results.Count(r => r.Check == RemoteAccess.Check.Wrong));
        Assert.Equal(6, results.Count(r => r.Check == RemoteAccess.Check.Locked));
        var row = await t.Verify().RemoteMachines.SingleAsync();
        Assert.Equal(now + RemoteMachine.LockFor, row.LockedUntil);
    }

    [Fact]
    public async Task Unattended_needs_the_switch_and_a_password()
    {
        var m = Machine();
        m.Unattended = true;
        Assert.False(m.UnattendedActive);
        using var t = new TestDb();
        t.Db.RemoteMachines.Add(m);
        await t.Db.SaveChangesAsync();
        Assert.Equal(RemoteAccess.Check.NotSet, (await RemoteAccess.VerifyAsync(t.Db, m.Id, "anything", Hasher, DateTime.UtcNow, default)).Check);
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

    private static async Task<RemoteMachine> StoredAsync(TestDb t, string password)
    {
        var m = Machine(password);
        t.Db.RemoteMachines.Add(m);
        await t.Db.SaveChangesAsync();
        return m;
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
