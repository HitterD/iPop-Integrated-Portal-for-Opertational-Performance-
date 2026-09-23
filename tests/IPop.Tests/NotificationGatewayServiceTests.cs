using IPop.Application.Auth;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

public class NotificationGatewayServiceTests
{
    private static NotificationEnqueueRequest BulkReq(string key = "k1", NotificationTarget? target = null) => new(
        IdempotencyKey: key,
        Priority: "normal",
        EventType: "payroll.ready",
        Title: "Payroll is ready",
        Body: "Your payslip is ready.",
        Target: target ?? new NotificationTarget(EmployeeNik: "A"));

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => $"h:{password}";
        public bool Verify(string password, string hash) => hash == $"h:{password}";
    }

    /// No-op heartbeat broadcaster — delivery tests assert the DB, not the SignalR fan-out
    /// (that's covered at the endpoint/broadcaster layer instead).
    private sealed class NoopBroadcaster : INotificationRealtimeBroadcaster
    {
        public Task BroadcastAsync(IReadOnlyList<NotificationDeliveryState> deliveries, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingBroadcaster : INotificationRealtimeBroadcaster
    {
        public List<NotificationDeliveryState> Broadcasted { get; } = [];
        public Task BroadcastAsync(IReadOnlyList<NotificationDeliveryState> deliveries, CancellationToken ct = default)
        {
            Broadcasted.AddRange(deliveries);
            return Task.CompletedTask;
        }
    }

    private static NotificationGatewayService Svc(TestDb t, IPasswordHasher? hasher = null,
        INotificationRealtimeBroadcaster? broadcaster = null)
        => new(t.Verify(), hasher ?? new FakeHasher(), NullLogger<NotificationGatewayService>.Instance,
            broadcaster ?? new NoopBroadcaster());

    private static NotificationGatewayClient AddClient(
        TestDb t, string clientId = "hr", string? secretHash = null,
        string? allowedIps = null, string? allowedEventTypes = null,
        int rateLimit = 0, int maxRecipients = 1000, bool active = true)
        => t.AddNotificationClient(clientId, secretHash ?? "h:secret123", allowedIps, allowedEventTypes, rateLimit, maxRecipients, active);

    // ── Auth ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Authenticate_ValidCredentials_ReturnsClient()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var svc = Svc(t);

        var info = await svc.AuthenticateClientAsync(c.ClientId, "secret123", "10.0.0.1", default);

        Assert.NotNull(info);
        Assert.Equal(c.Id, info!.Id);
    }

    [Fact]
    public async Task Authenticate_WrongSecret_ReturnsNull()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var svc = Svc(t);

        Assert.Null(await svc.AuthenticateClientAsync(c.ClientId, "wrong", null, default));
    }

    [Fact]
    public async Task Authenticate_InactiveClient_ReturnsNull()
    {
        using var t = new TestDb();
        var c = AddClient(t, active: false);
        var svc = Svc(t);

        Assert.Null(await svc.AuthenticateClientAsync(c.ClientId, "secret123", null, default));
    }

    [Fact]
    public async Task Authenticate_IpNotAllowed_ReturnsNull()
    {
        using var t = new TestDb();
        var c = AddClient(t, allowedIps: "10.1.1.1");
        var svc = Svc(t);

        Assert.Null(await svc.AuthenticateClientAsync(c.ClientId, "secret123", "10.2.2.2", default));
    }

    [Fact]
    public async Task Authenticate_IpAllowed_WhenListed()
    {
        using var t = new TestDb();
        var c = AddClient(t, allowedIps: "10.1.1.1, 10.2.2.2");
        var svc = Svc(t);

        Assert.NotNull(await svc.AuthenticateClientAsync(c.ClientId, "secret123", "10.2.2.2", default));
    }

    // ── Targeting expansion ────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_EmployeeTarget_CreatesOneDelivery()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var user = t.AddUser("A");
        var svc = Svc(t);

        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(EmployeeNik: user.Nik)), default);

        Assert.True(result.Success);
        Assert.Equal(1, result.DeliveryCount);
        var delivery = t.Verify().NotificationDeliveries.Single();
        Assert.Equal(user.Id, delivery.UserId);
        Assert.Equal("delivered", delivery.Status); // v1 stub: delivered in-line
        Assert.True(delivery.DeliveredAt is not null);
    }

    [Fact]
    public async Task Enqueue_AllTarget_TargetsAllActiveUsers()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        t.AddUser("A");
        t.AddUser("B");
        t.AddUser("C", active: false); // inactive must be excluded
        var svc = Svc(t);

        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(All: true)), default);

        Assert.True(result.Success);
        Assert.Equal(2, result.DeliveryCount);
    }

    [Fact]
    public async Task Enqueue_RoleTarget_ExpandsToRoleMembers()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var role = new Role { Id = Guid.NewGuid(), Name = "Payroll" };
        var user = t.AddUser("PR-1");
        t.Db.Roles.Add(role);
        t.Db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        t.Db.SaveChanges();
        var svc = Svc(t);

        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(Role: "Payroll")), default);

        Assert.True(result.Success);
        Assert.Equal(1, result.DeliveryCount);
    }

    [Fact]
    public async Task Enqueue_UnknownEmployee_ReturnsTargetNotFound()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var svc = Svc(t);

        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(EmployeeNik: "nobody")), default);

        Assert.False(result.Success);
        Assert.Equal(NotificationErrors.TargetNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Enqueue_TooManyRecipients_Rejected()
    {
        using var t = new TestDb();
        var c = AddClient(t, maxRecipients: 1);
        t.AddUser("A");
        t.AddUser("B");
        var svc = Svc(t);

        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(All: true)), default);

        Assert.False(result.Success);
        Assert.Equal(NotificationErrors.TooManyRecipients, result.ErrorCode);
    }

    [Fact]
    public async Task Enqueue_InvalidPriority_Rejected()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var svc = Svc(t);

        var req = BulkReq() with { Priority = "urgent" };
        var result = await svc.EnqueueAsync(c.Id, req, default);

        Assert.False(result.Success);
        Assert.Equal(NotificationErrors.InvalidPriority, result.ErrorCode);
    }

    [Fact]
    public async Task Enqueue_HighPriority_Accepted()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var u = t.AddUser("A");
        var svc = Svc(t);

        var req = BulkReq() with { Priority = "high" };
        var result = await svc.EnqueueAsync(c.Id, req, default);

        Assert.True(result.Success);
        Assert.True(result.DeliveryCount >= 1);
    }

    [Fact]
    public async Task Enqueue_NullTarget_ReturnsNoTargetSpecified()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        t.AddUser("A");
        var svc = Svc(t);

        // System-to-system: other apps may omit the target field entirely.
        var req = BulkReq() with { Target = null! };
        var result = await svc.EnqueueAsync(c.Id, req, default);

        Assert.False(result.Success);
        Assert.Equal(NotificationErrors.NoTargetSpecified, result.ErrorCode);
    }

    [Fact]
    public async Task Enqueue_WildcardEventType_AllowsAnyEvent()
    {
        using var t = new TestDb();
        var c = AddClient(t, allowedEventTypes: "*");
        t.AddUser("A");
        var svc = Svc(t);

        var req = BulkReq() with { EventType = "anything.goes" };
        var result = await svc.EnqueueAsync(c.Id, req, default);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Enqueue_ExplicitList_RejectsUnlistedWhileWildcardAllows()
    {
        using var t = new TestDb();
        var c = AddClient(t, allowedEventTypes: "payroll.ready, hr.update");
        t.AddUser("A");
        var svc = Svc(t);

        var unlisted = await svc.EnqueueAsync(c.Id, BulkReq() with { EventType = "sale.new" }, default);
        Assert.False(unlisted.Success);
        Assert.Equal(NotificationErrors.EventTypeNotAllowed, unlisted.ErrorCode);

        var listed = await svc.EnqueueAsync(c.Id, BulkReq("k2") with { EventType = "hr.update" }, default);
        Assert.True(listed.Success);
    }

    [Fact]
    public async Task Enqueue_EventTypeNotAllowed_WhenNotInList()
    {
        using var t = new TestDb();
        var c = AddClient(t, allowedEventTypes: "payroll.ready");
        var svc = Svc(t);

        var req = BulkReq() with { EventType = "sale.new" };
        var result = await svc.EnqueueAsync(c.Id, req, default);

        Assert.False(result.Success);
        Assert.Equal(NotificationErrors.EventTypeNotAllowed, result.ErrorCode);
    }

    // ── Idempotency ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_SameIdempotencyKey_ReturnsOriginalRequest()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var u = t.AddUser("A");
        var svc = Svc(t);
        var target = new NotificationTarget(EmployeeNik: u.Nik);

        var first = await svc.EnqueueAsync(c.Id, BulkReq("same-key", target), default);
        var second = await svc.EnqueueAsync(c.Id, BulkReq("same-key", target), default);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(1, second.DeliveryCount); // no duplicate deliveries
    }

    // ── Lifecycle transitions ──────────────────────────────────────────────────

    [Fact]
    public async Task MarkRead_TransitionsToRead()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var u = t.AddUser("A");
        var svc = Svc(t);
        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(EmployeeNik: u.Nik)), default);

        var state = await svc.MarkReadAsync(result.RequestId, u.Id, default);

        Assert.Equal("read", state!.Status);
        Assert.NotNull(state.ReadAt);
    }

    [Fact]
    public async Task MarkAcknowledged_ImpliesReadAndTerminal()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var u = t.AddUser("A");
        var svc = Svc(t);
        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(EmployeeNik: u.Nik)), default);

        var state = await svc.MarkAcknowledgedAsync(result.RequestId, u.Id, default);
        // acknowledged must also be read
        Assert.Equal("acknowledged", state!.Status);
        Assert.NotNull(state.ReadAt);
        Assert.NotNull(state.AcknowledgedAt);

        // second ack is idempotent (no change)
        var again = await svc.MarkAcknowledgedAsync(result.RequestId, u.Id, default);
        Assert.Equal("acknowledged", again!.Status);
    }

    [Fact]
    public async Task MarkRead_UnknownDelivery_ReturnsNull()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var u = t.AddUser("A");
        var svc = Svc(t);
        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(EmployeeNik: u.Nik)), default);

        Assert.Null(await svc.MarkReadAsync(result.RequestId, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Retry_RepeatedRetries_DeadLetterAfterBudget()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var u = t.AddUser("A");
        var svc = Svc(t);
        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(EmployeeNik: u.Nik)), default);

        // Delivery already delivered (attempt=1). Retry bumps attempt; after MaxAttempts it fails.
        var state = result.Status; // delivered
        for (var i = 0; i < 3; i++)
            state = (await svc.RetryDeliveryAsync(result.RequestId, u.Id, default))!.Status;

        Assert.Equal("failed", state);
    }

    [Fact]
    public async Task Enqueue_OnlineUser_BroadcastsSignalRDelivered()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var u = t.AddUser("A");
        u.PresenceLastSeenAt = DateTime.UtcNow; // online → channel signalr
        t.Db.SaveChanges();
        var rec = new RecordingBroadcaster();
        var svc = Svc(t, broadcaster: rec);

        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(EmployeeNik: u.Nik)), default);

        Assert.True(result.Success);
        var broadcast = Assert.Single(rec.Broadcasted);
        Assert.Equal(u.Id, broadcast.UserId);
        Assert.Equal(NotificationStatus.Delivered, broadcast.Status);
        Assert.Equal(NotificationChannel.SignalR, broadcast.Channel);
        Assert.Equal("Payroll is ready", broadcast.Title);
        Assert.Equal("Your payslip is ready.", broadcast.Body);
        Assert.Equal("payroll.ready", broadcast.EventType);
        Assert.Equal("normal", broadcast.Priority);
    }

    [Fact]
    public async Task Enqueue_OfflineUser_DoesNotBroadcast()
    {
        using var t = new TestDb();
        var c = AddClient(t);
        var u = t.AddUser("A"); // offline (PresenceLastSeenAt null) → channel push
        t.Db.SaveChanges();
        var rec = new RecordingBroadcaster();
        var svc = Svc(t, broadcaster: rec);

        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(EmployeeNik: u.Nik)), default);

        Assert.True(result.Success);
        Assert.Empty(rec.Broadcasted);
    }

    // ── Jam senyap (Fase 4) — evaluasi backend dengan zona waktu pengguna. ──

    private static NotificationPreference Pref(
        bool enabled = true, string start = "22:00", string end = "07:00",
        string quietDays = "", bool allowImportant = true)
        => new()
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(),
            QuietHoursEnabled = enabled, QuietStart = start, QuietEnd = end,
            QuietDays = quietDays, AllowImportant = allowImportant,
            MutedTypes = "", UpdatedAt = DateTime.UtcNow,
        };

    private static readonly TimeZoneInfo Wib = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

    [Fact]
    public void QuietHours_InWindow_WIB_ReturnsTrue()
    {
        // 2026-09-21 16:00 UTC = 23:00 WIB (Senin) — masuk rentang 22:00–07:00.
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        Assert.True(NotificationGatewayService.IsInQuietHours(Pref(), utc, Wib));
    }

    [Fact]
    public void QuietHours_OutsideWindow_WIB_ReturnsFalse()
    {
        // 2026-09-21 05:00 UTC = 12:00 WIB (Senin) — di luar rentang.
        var utc = new DateTime(2026, 9, 21, 5, 0, 0, DateTimeKind.Utc);
        Assert.False(NotificationGatewayService.IsInQuietHours(Pref(), utc, Wib));
    }

    [Fact]
    public void QuietHours_Disabled_ReturnsFalse()
    {
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        Assert.False(NotificationGatewayService.IsInQuietHours(Pref(enabled: false), utc, Wib));
    }

    [Fact]
    public void QuietHours_QuietDays_DayNotIncluded_ReturnsFalse()
    {
        // Senin 23:00 WIB; jam senyap hanya Selasa → tidak menekan.
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        Assert.False(NotificationGatewayService.IsInQuietHours(Pref(quietDays: "tue"), utc, Wib));
    }

    [Fact]
    public void QuietHours_QuietDays_DayIncluded_ReturnsTrue()
    {
        // Senin 23:00 WIB; jam senyap termasuk Senin → menekan.
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        Assert.True(NotificationGatewayService.IsInQuietHours(Pref(quietDays: "mon"), utc, Wib));
    }

    [Fact]
    public void QuietHours_OvernightRange_MorningSide_ReturnsTrue()
    {
        // 2026-09-21 23:30 UTC = Selasa 06:30 WIB — sisi pagi dari rentang 22:00–07:00.
        var utc = new DateTime(2026, 9, 21, 23, 30, 0, DateTimeKind.Utc);
        Assert.True(NotificationGatewayService.IsInQuietHours(Pref(), utc, Wib));
    }

    [Fact]
    public async Task Enqueue_QuietHours_DeferredToPush()
    {
        // Integrasi: pengguna dalam jam senyap → notifikasi normal ditahan (channel push,
        // tidak dibroadcast via SignalR). Ini membuktikan backend tetap menekan walau
        // klien gagal mengevaluasi zona waktu (kasus fail-open frontend).
        using var t = new TestDb();
        var c = AddClient(t);
        var u = t.AddUser("A");
        // Paksa pengguna online agar jalur default-nya SignalR — jam senyap yang mengubahnya.
        u.PresenceLastSeenAt = DateTime.UtcNow;
        var now = DateTime.UtcNow;
        var wibNow = TimeZoneInfo.ConvertTimeFromUtc(now, Wib);
        t.Db.NotificationPreferences.Add(new NotificationPreference
        {
            Id = Guid.NewGuid(), UserId = u.Id,
            QuietHoursEnabled = true,
            QuietStart = wibNow.AddHours(-1).ToString("HH:mm"),
            QuietEnd = wibNow.AddHours(1).ToString("HH:mm"),
            QuietDays = "", AllowImportant = true, MutedTypes = "",
            UpdatedAt = now,
        });
        t.Db.SaveChanges();
        var rec = new RecordingBroadcaster();
        var svc = Svc(t, broadcaster: rec);

        var result = await svc.EnqueueAsync(c.Id, BulkReq(target: new NotificationTarget(EmployeeNik: u.Nik)), default);

        Assert.True(result.Success);
        var delivery = t.Db.NotificationDeliveries.Single();
        Assert.Equal("push", delivery.Channel); // ditahan — bukan signalr
        Assert.Empty(rec.Broadcasted);          // tidak ada fan-out real-time
    }

    [Fact]
    public async Task Enqueue_QuietHours_HighPriority_BreaksThrough()
    {
        // Integrasi: AllowImportant=true → prioritas high tetap disiarkan walau jam senyap.
        using var t = new TestDb();
        var c = AddClient(t);
        var u = t.AddUser("A");
        u.PresenceLastSeenAt = DateTime.UtcNow;
        var now = DateTime.UtcNow;
        var wibNow = TimeZoneInfo.ConvertTimeFromUtc(now, Wib);
        t.Db.NotificationPreferences.Add(new NotificationPreference
        {
            Id = Guid.NewGuid(), UserId = u.Id,
            QuietHoursEnabled = true,
            QuietStart = wibNow.AddHours(-1).ToString("HH:mm"),
            QuietEnd = wibNow.AddHours(1).ToString("HH:mm"),
            QuietDays = "", AllowImportant = true, MutedTypes = "",
            UpdatedAt = now,
        });
        t.Db.SaveChanges();
        var rec = new RecordingBroadcaster();
        var svc = Svc(t, broadcaster: rec);

        var req = BulkReq(target: new NotificationTarget(EmployeeNik: u.Nik)) with { Priority = "high" };
        var result = await svc.EnqueueAsync(c.Id, req, default);

        Assert.True(result.Success);
        var delivery = t.Db.NotificationDeliveries.Single();
        Assert.Equal("signalr", delivery.Channel); // tidak ditahan
        Assert.Single(rec.Broadcasted);
    }
}
