using System.Text.Json;
using IPop.Application.Calls;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications.Push;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Web Push through the durable outbox: who gets a Windows toast, what it says, per-browser
/// retry/expiry, and cleanup of dead subscriptions. A fake transport stands in for the push
/// service; "accepted" only ever means the provider took the message.
public class PushDispatcherTests
{
    private sealed class RecordingTransport : IWebPushTransport
    {
        public List<(PushSubscription Sub, PushPayload Payload, bool Urgent, TimeSpan Ttl)> Sent { get; } = [];
        /// Endpoint → scripted outcomes (consumed in order; the last repeats). Default: accepted.
        public Dictionary<string, Queue<PushSendOutcome>> Script { get; } = [];
        public bool IsConfigured => true;

        public Task<PushSendOutcome> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct)
        {
            Sent.Add((sub, JsonSerializer.Deserialize<PushPayload>(json, PushDispatcher.Json)!, urgent, ttl));
            if (Script.TryGetValue(sub.Endpoint, out var q) && q.Count > 0)
                return Task.FromResult(q.Count > 1 ? q.Dequeue() : q.Peek());
            return Task.FromResult(PushSendOutcome.Accepted);
        }

        public void Fail(string endpoint, params PushSendOutcome[] outcomes) => Script[endpoint] = new(outcomes);
        public IEnumerable<Guid> Recipients => Sent.Select(s => s.Sub.UserId);
    }

    private static readonly IDataProtectionProvider Dp = new EphemeralDataProtectionProvider();

    private static PushDispatcher Dispatcher(TestDb t, RecordingTransport tr) =>
        new(t.Verify(), tr, Dp, NullLogger<PushDispatcher>.Instance);

    /// Enqueue like a producer, then run worker passes at the given time.
    private static async Task<Guid> Enqueue(TestDb t, string kind, Guid? refId, IEnumerable<Guid>? users = null,
        string? eventType = null, string? priority = null, string? title = null)
    {
        using var db = t.Verify();
        return await new PushQueue().EnqueueAsync(db, kind, refId, default, users, eventType, priority, title);
    }

    private static Task Run(TestDb t, RecordingTransport tr, DateTime? at = null) =>
        Dispatcher(t, tr).ProcessAsync(at ?? DateTime.UtcNow, default);

    private static async Task<RecordingTransport> Push(TestDb t, string kind, Guid? refId, IEnumerable<Guid>? users = null,
        string? eventType = null, string? priority = null, string? title = null)
    {
        var tr = new RecordingTransport();
        await Enqueue(t, kind, refId, users, eventType, priority, title);
        await Run(t, tr);
        return tr;
    }

    private static PushSubscription Subscribe(TestDb t, Guid userId, string? endpoint = null)
    {
        var s = new PushSubscription
        {
            Id = Guid.NewGuid(), UserId = userId, Endpoint = endpoint ?? $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}",
            P256dh = "p", Auth = "a", Label = "Chrome · Windows", CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        };
        t.Db.PushSubscriptions.Add(s);
        t.Db.SaveChanges();
        return s;
    }

    private static void Prefs(TestDb t, Guid userId, Action<NotificationPreference> configure)
    {
        var p = new NotificationPreference { Id = Guid.NewGuid(), UserId = userId, UpdatedAt = DateTime.UtcNow };
        configure(p);
        t.Db.NotificationPreferences.Add(p);
        t.Db.SaveChanges();
    }

    private static List<PushDelivery> Deliveries(TestDb t) => t.Verify().PushDeliveries.AsNoTracking().ToList();

    // ── Chat messages ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatMessage_ToastsOtherMembers_NotTheSender()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B"); var c = t.AddUser("C");
        var room = t.AddRoom("group", "Tim IT");
        foreach (var u in new[] { a, b, c }) { t.AddMember(room.Id, u.Id); Subscribe(t, u.Id); }
        var msg = t.AddMessage(room.Id, a.Id, "Halo semua");

        var tr = await Push(t, "chat", msg.Id);

        Assert.Equal(new[] { b.Id, c.Id }.OrderBy(x => x), tr.Recipients.OrderBy(x => x));
        var p = tr.Sent[0].Payload;
        Assert.Equal("chat", p.Kind);
        Assert.Equal("User A · Tim IT", p.Title);
        Assert.Equal("Halo semua", p.Body);
        Assert.Equal($"chat:{room.Id}", p.Tag);
        Assert.Equal($"/chat/{room.Id}", p.Url);
        Assert.All(Deliveries(t), d => Assert.Equal(PushDispatcher.Status.Accepted, d.Status));
    }

    [Fact]
    public async Task ChatMessage_SkipsMutedArchivedLeftAndBlockers()
    {
        using var t = new TestDb();
        var sender = t.AddUser("S");
        var muted = t.AddUser("M"); var archived = t.AddUser("R"); var left = t.AddUser("L");
        var blocker = t.AddUser("B"); var muteExpired = t.AddUser("E"); var ok = t.AddUser("O");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, sender.Id);
        foreach (var u in new[] { muted, archived, blocker, muteExpired, ok }) t.AddMember(room.Id, u.Id);
        t.AddMember(room.Id, left.Id, leftAt: DateTime.UtcNow.AddDays(-1));
        foreach (var u in new[] { muted, archived, left, blocker, muteExpired, ok }) Subscribe(t, u.Id);
        t.AddSettings(muted.Id, room.Id, s => s.IsMuted = true);
        t.AddSettings(archived.Id, room.Id, s => s.IsArchived = true);
        t.AddSettings(muteExpired.Id, room.Id, s => { s.IsMuted = true; s.MuteExpiresAt = DateTime.UtcNow.AddMinutes(-1); });
        t.Db.UserBlocks.Add(new UserBlock { UserId = blocker.Id, BlockedUserId = sender.Id, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var msg = t.AddMessage(room.Id, sender.Id, "x");

        var tr = await Push(t, "chat", msg.Id);

        Assert.Equal(new[] { muteExpired.Id, ok.Id }.OrderBy(x => x), tr.Recipients.OrderBy(x => x));
    }

    [Fact]
    public async Task ChatMessage_SkipsSystemMessages()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var room = t.AddRoom();
        t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id); Subscribe(t, b.Id);
        var msg = t.AddMessage(room.Id, a.Id, "A menambahkan B", type: "system");

        var tr = await Push(t, "chat", msg.Id);

        Assert.Empty(tr.Sent);
    }

    [Fact]
    public async Task ChatMessage_MentionedMember_GetsNoPlainToast()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B"); var c = t.AddUser("C");
        var room = t.AddRoom("group", "G");
        foreach (var u in new[] { a, b, c }) { t.AddMember(room.Id, u.Id); Subscribe(t, u.Id); }
        var msg = t.AddMessage(room.Id, a.Id, "@B tolong cek");
        t.Db.ChatMentions.Add(new ChatMention { Id = Guid.NewGuid(), MessageId = msg.Id, UserId = b.Id, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        var tr = await Push(t, "chat", msg.Id);

        Assert.Equal(new[] { c.Id }, tr.Recipients);
    }

    [Fact]
    public async Task ChatMessage_MutedChatType_AndPreviewOff_AreHonoured()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var mutedType = t.AddUser("M"); var privateUser = t.AddUser("P");
        var room = t.AddRoom();
        foreach (var u in new[] { a, mutedType, privateUser }) { t.AddMember(room.Id, u.Id); Subscribe(t, u.Id); }
        Prefs(t, mutedType.Id, p => p.MutedTypes = "chat");
        Prefs(t, privateUser.Id, p => p.PushShowPreview = false);
        var msg = t.AddMessage(room.Id, a.Id, "gaji bulan ini");

        var tr = await Push(t, "chat", msg.Id);

        var only = Assert.Single(tr.Sent);
        Assert.Equal(privateUser.Id, only.Sub.UserId);
        Assert.Equal("New message", only.Payload.Body);
        Assert.Equal("User A", only.Payload.Title); // DM: sender name only
    }

    [Fact]
    public async Task EmergencyMessage_BypassesRoomMute_AndIsSticky()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var room = t.AddRoom("group", "Gedung");
        t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id); Subscribe(t, b.Id);
        t.AddSettings(b.Id, room.Id, s => s.IsMuted = true);
        Prefs(t, b.Id, p => p.MutedTypes = "chat");
        var msg = t.AddMessage(room.Id, a.Id, "Evakuasi sekarang", type: "emergency");

        var tr = await Push(t, "chat", msg.Id);

        var only = Assert.Single(tr.Sent);
        Assert.True(only.Payload.RequireInteraction);
        Assert.True(only.Urgent);
    }

    [Theory]
    [InlineData("[poll]{}", 0, "📊 Poll")]
    [InlineData("", 1, "📎 Attachment")]
    [InlineData("", 3, "📎 3 attachments")]
    [InlineData("", 0, "New message")]
    public void ChatPreview_NamesNonTextContent(string content, int files, string expected) =>
        Assert.Equal(expected, PushDispatcher.ChatPreview(content, files));

    [Fact]
    public void ChatPreview_TruncatesTo120()
    {
        var text = new string('x', 300);
        Assert.True(PushDispatcher.ChatPreview(text, 0).Length <= 121);
    }

    // ── Outbox mechanics ───────────────────────────────────────────────────────

    [Fact]
    public async Task Payload_CarriesIds_ApiBase_AndAccountBinding()
    {
        using var t = new TestDb();
        var u = t.AddUser("A");
        var s = Subscribe(t, u.Id);
        s.ApiBase = "https://ipop.santos.co.id";
        t.Db.SaveChanges();

        var tr = await Push(t, "test", null, [u.Id]);

        var p = Assert.Single(tr.Sent).Payload;
        var d = Assert.Single(Deliveries(t));
        Assert.Equal("https://ipop.santos.co.id", p.ApiBase);
        Assert.Equal(d.Id.ToString("N"), p.DeliveryId);
        Assert.Equal(d.JobId.ToString("N"), p.JobId);
        Assert.Equal(PushDispatcher.AcctOf(u.Id), p.Acct);
        Assert.Matches("^[0-9a-f]{16}$", p.Acct!);
        Assert.Equal("test", p.Kind);
    }

    [Fact]
    public void AcctOf_MatchesTheServiceWorkerContract()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("ipop-push:0f8fad5b-d9cb-469f-a165-70867728950e")))[..16];
        Assert.Equal(expected, PushDispatcher.AcctOf(id));
    }

    [Fact]
    public async Task EnqueueIsDurable_WorkerRestartStillSends()
    {
        using var t = new TestDb();
        var u = t.AddUser("A"); Subscribe(t, u.Id);
        await Enqueue(t, "test", null, [u.Id]);
        Assert.Equal(1, await t.Verify().PushJobs.CountAsync()); // persisted before any worker ran

        var tr = new RecordingTransport(); // a "fresh process": new dispatcher, new transport
        await Run(t, tr);

        Assert.Single(tr.Sent);
    }

    [Fact]
    public async Task OneFailingBrowser_IsRetried_WithoutResendingTheAcceptedOne()
    {
        using var t = new TestDb();
        var u = t.AddUser("A");
        var good = Subscribe(t, u.Id);
        var flaky = Subscribe(t, u.Id);
        var tr = new RecordingTransport();
        tr.Fail(flaky.Endpoint, PushSendOutcome.FromStatus(503), PushSendOutcome.Accepted);
        var now = DateTime.UtcNow.AddSeconds(1); // just after the enqueue below
        await Enqueue(t, "test", null, [u.Id]);

        await Run(t, tr, now);
        var flakyRow = Deliveries(t).Single(d => d.SubscriptionId == flaky.Id);
        Assert.Equal(PushDispatcher.Status.Retrying, flakyRow.Status);
        Assert.Equal("server-error", flakyRow.Code);

        await Run(t, tr, now.AddSeconds(1)); // not due yet
        Assert.Equal(2, tr.Sent.Count);

        await Run(t, tr, flakyRow.NextAttemptAt);
        Assert.Equal(3, tr.Sent.Count);
        Assert.Equal(1, tr.Sent.Count(s => s.Sub.Id == good.Id)); // never resent
        var rows = Deliveries(t);
        Assert.All(rows, d => Assert.Equal(PushDispatcher.Status.Accepted, d.Status));
        // Same delivery id across retries.
        var flakyIds = tr.Sent.Where(s => s.Sub.Id == flaky.Id).Select(s => s.Payload.DeliveryId).ToList();
        Assert.Equal(2, flakyIds.Count);
        Assert.Equal(flakyRow.Id.ToString("N"), Assert.Single(flakyIds.Distinct()));
    }

    [Fact]
    public async Task RateLimit_HonoursRetryAfter()
    {
        using var t = new TestDb();
        var u = t.AddUser("A"); var s = Subscribe(t, u.Id);
        var tr = new RecordingTransport();
        tr.Fail(s.Endpoint, PushSendOutcome.FromStatus(429, TimeSpan.FromSeconds(90)));
        var now = DateTime.UtcNow.AddSeconds(1); // just after the enqueue below
        await Enqueue(t, "test", null, [u.Id]);

        await Run(t, tr, now);

        var d = Assert.Single(Deliveries(t));
        Assert.Equal(PushDispatcher.Status.Retrying, d.Status);
        Assert.Equal("rate-limited", d.Code);
        Assert.True(d.NextAttemptAt >= now.AddSeconds(89));
    }

    [Fact]
    public async Task Retry_NeverOutlivesTheOriginalDeadline()
    {
        using var t = new TestDb();
        var u = t.AddUser("A"); var s = Subscribe(t, u.Id);
        var tr = new RecordingTransport();
        // Retry-After beyond the 10-minute test deadline → expired, not rescheduled.
        tr.Fail(s.Endpoint, PushSendOutcome.FromStatus(429, TimeSpan.FromHours(1)));
        await Enqueue(t, "test", null, [u.Id]);

        await Run(t, tr);

        var d = Assert.Single(Deliveries(t));
        Assert.Equal(PushDispatcher.Status.Expired, d.Status);
        Assert.True(tr.Sent[0].Ttl <= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task TransientFailures_StopAfterMaxAttempts()
    {
        using var t = new TestDb();
        var u = t.AddUser("A"); var s = Subscribe(t, u.Id);
        var room = t.AddRoom(); t.AddMember(room.Id, t.AddUser("S").Id); t.AddMember(room.Id, u.Id);
        var msg = t.AddMessage(room.Id, t.Db.ChatRoomMembers.First(m => m.UserId != u.Id).UserId, "hi");
        var tr = new RecordingTransport();
        tr.Fail(s.Endpoint, new PushSendOutcome(PushSendResult.Transient, "network"));
        var now = DateTime.UtcNow.AddSeconds(1); // just after the enqueue below
        await Enqueue(t, "chat", msg.Id);

        for (var i = 0; i < 20; i++) { await Run(t, tr, now); now = now.AddMinutes(10); }

        var d = Assert.Single(Deliveries(t));
        Assert.Equal(PushDispatcher.Status.Failed, d.Status);
        Assert.Equal("max-attempts", d.Code);
        Assert.Equal(PushDelivery.MaxAttempts, tr.Sent.Count);
    }

    [Fact]
    public async Task Gone_DeletesSubscription_AuthError_KeepsIt_AndNeitherRetries()
    {
        using var t = new TestDb();
        var u = t.AddUser("A");
        var dead = Subscribe(t, u.Id);
        var auth = Subscribe(t, u.Id);
        var tr = new RecordingTransport();
        tr.Fail(dead.Endpoint, PushSendOutcome.FromStatus(410));
        tr.Fail(auth.Endpoint, PushSendOutcome.FromStatus(403));
        var now = DateTime.UtcNow.AddSeconds(1); // just after the enqueue below
        await Enqueue(t, "test", null, [u.Id]);

        await Run(t, tr, now);
        await Run(t, tr, now.AddMinutes(5));

        Assert.Equal(2, tr.Sent.Count);
        var rows = Deliveries(t);
        Assert.Equal(PushDispatcher.Status.Gone, rows.Single(d => d.SubscriptionId == dead.Id).Status);
        var a = rows.Single(d => d.SubscriptionId == auth.Id);
        Assert.Equal(PushDispatcher.Status.AuthError, a.Status);
        Assert.Equal("auth-403", a.Code);
        Assert.Equal(new[] { auth.Id }, await t.Verify().PushSubscriptions.Select(s => s.Id).ToListAsync());
    }

    [Fact]
    public async Task Retry_RechecksMembership_AndPreviewPrivacy()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B"); var c = t.AddUser("C");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, a.Id);
        var bm = t.AddMember(room.Id, b.Id);
        t.AddMember(room.Id, c.Id);
        var sb = Subscribe(t, b.Id); var sc = Subscribe(t, c.Id);
        var msg = t.AddMessage(room.Id, a.Id, "rahasia");
        var tr = new RecordingTransport();
        tr.Fail(sb.Endpoint, PushSendOutcome.FromStatus(500));
        tr.Fail(sc.Endpoint, PushSendOutcome.FromStatus(500), PushSendOutcome.Accepted);
        var now = DateTime.UtcNow.AddSeconds(1); // just after the enqueue below
        await Enqueue(t, "chat", msg.Id);
        await Run(t, tr, now);

        // Before the retry: B left the room, C turned previews off.
        bm.LeftAt = DateTime.UtcNow; t.Db.SaveChanges();
        Prefs(t, c.Id, p => p.PushShowPreview = false);
        await Run(t, tr, now.AddMinutes(1));

        Assert.Equal(3, tr.Sent.Count);
        Assert.Equal("New message", tr.Sent[2].Payload.Body);
        var rows = Deliveries(t);
        var rb = rows.Single(d => d.SubscriptionId == sb.Id);
        Assert.Equal(PushDispatcher.Status.Suppressed, rb.Status);
        Assert.Equal("not-authorized", rb.Code);
    }

    [Fact]
    public async Task Retry_SubscriptionMovedToAnotherAccount_IsNotSent()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var other = t.AddUser("X");
        var s = Subscribe(t, a.Id);
        var tr = new RecordingTransport();
        tr.Fail(s.Endpoint, PushSendOutcome.FromStatus(502), PushSendOutcome.Accepted);
        var now = DateTime.UtcNow.AddSeconds(1); // just after the enqueue below
        await Enqueue(t, "test", null, [a.Id]);
        await Run(t, tr, now);

        s.UserId = other.Id; t.Db.SaveChanges(); // same browser, now signed in as X
        await Run(t, tr, now.AddMinutes(1));

        Assert.Single(tr.Sent);
        var d = Assert.Single(Deliveries(t));
        Assert.Equal(PushDispatcher.Status.Suppressed, d.Status);
        Assert.Equal("rebound", d.Code);
    }

    [Fact]
    public async Task ExpiredLease_IsReclaimed_AfterACrash()
    {
        using var t = new TestDb();
        var u = t.AddUser("A"); Subscribe(t, u.Id);
        var now = DateTime.UtcNow.AddSeconds(1); // just after the enqueue below
        await Enqueue(t, "test", null, [u.Id]);
        await Dispatcher(t, new RecordingTransport()).ExpandDueJobsAsync(now, default);
        // Simulate a worker that claimed the row and died before sending.
        using (var db = t.Verify())
            await db.PushDeliveries.ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, PushDispatcher.Status.Sending)
                .SetProperty(d => d.NextAttemptAt, now + PushDispatcher.Lease));

        var tr = new RecordingTransport();
        await Run(t, tr, now.AddSeconds(30));
        Assert.Empty(tr.Sent); // lease still held
        await Run(t, tr, now + PushDispatcher.Lease);
        Assert.Single(tr.Sent);
    }

    [Fact]
    public async Task Purge_DropsFinishedHistoryPastRetention_KeepsPending()
    {
        using var t = new TestDb();
        var u = t.AddUser("A"); var s = Subscribe(t, u.Id);
        var old = DateTime.UtcNow.AddDays(-8);
        var tr = new RecordingTransport();
        await Enqueue(t, "test", null, [u.Id]);
        await Run(t, tr);
        using (var db = t.Verify()) await db.PushJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.CreatedAt, old));

        var n = await Dispatcher(t, tr).PurgeAsync(DateTime.UtcNow, default);

        Assert.Equal(1, n);
        Assert.Empty(Deliveries(t));
    }

    // ── Gateway notifications ──────────────────────────────────────────────────

    private static NotificationRequest Request(TestDb t, string eventType, string body, string? url, string priority = "normal")
    {
        var client = t.AddNotificationClient($"c{Guid.NewGuid():N}", "x");
        var r = new NotificationRequest
        {
            Id = Guid.NewGuid(), ClientId = client.Id, IdempotencyKey = Guid.NewGuid().ToString(), Priority = priority,
            EventType = eventType, Title = "T", Body = body, TargetUrl = url, Status = "delivered",
            AcceptedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NotificationRequests.Add(r);
        t.Db.SaveChanges();
        return r;
    }

    [Fact]
    public async Task Request_ChatMention_SharesTheRoomTag_AndUsesTitleOverride()
    {
        using var t = new TestDb();
        var u = t.AddUser("A"); Subscribe(t, u.Id);
        var room = t.AddRoom("group", "G"); t.AddMember(room.Id, u.Id);
        var req = Request(t, "chat.mention", "cek ini", $"/chat/{room.Id}?jump={Guid.NewGuid()}", "high");

        var tr = await Push(t, "notif", req.Id, [u.Id], "chat.mention", "high", "Budi mentioned you · G");

        var p = Assert.Single(tr.Sent).Payload;
        Assert.Equal($"chat:{room.Id}", p.Tag);
        Assert.Equal("Budi mentioned you · G", p.Title);
        Assert.Equal("cek ini", p.Body);
    }

    [Fact]
    public async Task Request_RechecksMutedType_AtDispatch()
    {
        using var t = new TestDb();
        var u = t.AddUser("A"); Subscribe(t, u.Id);
        Prefs(t, u.Id, p => p.MutedTypes = "news");
        var req = Request(t, "news.published", "Berita", "/news/1");

        var tr = await Push(t, "notif", req.Id, [u.Id], "news.published", "normal");

        Assert.Empty(tr.Sent);
    }

    // ── Preferences ────────────────────────────────────────────────────────────

    private static readonly DateTime Night = new(2026, 9, 30, 16, 0, 0, DateTimeKind.Utc); // 23:00 WIB

    [Fact]
    public void QuietHours_SilenceNormal_ButNotImportantWhenAllowed()
    {
        var pref = new NotificationPreference { QuietHoursEnabled = true, QuietStart = "22:00", QuietEnd = "07:00", AllowImportant = true };
        Assert.True(PushDispatcher.IsSilenced(pref, null, "news.published", "normal", Night));
        Assert.False(PushDispatcher.IsSilenced(pref, null, "news.published", "high", Night));
        pref.AllowImportant = false;
        Assert.True(PushDispatcher.IsSilenced(pref, null, "news.published", "high", Night));
    }

    [Fact]
    public void MutedType_MatchesModuleOrExactEvent()
    {
        var pref = new NotificationPreference { MutedTypes = "news,project.comment" };
        Assert.True(PushDispatcher.IsSilenced(pref, null, "news.published", "normal", Night));
        Assert.True(PushDispatcher.IsSilenced(pref, null, "project.comment", "normal", Night));
        Assert.False(PushDispatcher.IsSilenced(pref, null, "project.assigned", "normal", Night));
    }

    [Fact]
    public async Task Busy_silences_Windows_alerts_only_while_online_and_unexpired()
    {
        using var t = new TestDb();
        var busy = t.AddUser("BUSY"); busy.PresenceManualStatus = "busy"; busy.PresenceLastSeenAt = Night;
        var lapsed = t.AddUser("LAPSED"); lapsed.PresenceManualStatus = "busy"; lapsed.PresenceLastSeenAt = Night;
        lapsed.PresenceBusyUntilUtc = Night.AddMinutes(-1);
        var offline = t.AddUser("OFF"); offline.PresenceManualStatus = "busy"; offline.PresenceLastSeenAt = Night.AddHours(-1);
        var free = t.AddUser("FREE"); free.PresenceLastSeenAt = Night;
        await t.Db.SaveChangesAsync();

        var silenced = await PushDispatcher.SilencedAsync(t.Db, [busy.Id, lapsed.Id, offline.Id, free.Id],
            "news.published", "normal", Night, default);

        Assert.Equal([busy.Id], silenced.ToArray());
    }

    // ── Calls ──────────────────────────────────────────────────────────────────

    private static CallRoom AddCall(TestDb t, Guid caller, Guid callee, DateTime? ringAt = null)
    {
        var now = ringAt ?? DateTime.UtcNow;
        var room = new CallRoom
        {
            Id = Guid.NewGuid(), CreatorId = caller, Kind = CallKind.User, Status = CallStatus.Ringing,
            StartedAt = now, CreatedAt = now, UpdatedAt = now,
            Participants =
            [
                new CallParticipant { Id = Guid.NewGuid(), UserId = caller, Status = CallParticipantStatus.Accepted, JoinedAt = now },
                new CallParticipant { Id = Guid.NewGuid(), UserId = callee, Status = CallParticipantStatus.Invited, JoinedAt = now },
            ],
        };
        t.Db.CallRooms.Add(room);
        t.Db.SaveChanges();
        return room;
    }

    private static void Answer(TestDb t, CallRoom room, Guid callee)
    {
        room.Status = CallStatus.Active;
        room.Participants.Single(p => p.UserId == callee).Status = CallParticipantStatus.Accepted;
        t.Db.SaveChanges();
    }

    [Fact]
    public async Task Call_RingsOnce_ThenClosesOnceWhenAnswered()
    {
        using var t = new TestDb();
        var caller = t.AddUser("C"); var callee = t.AddUser("E");
        Subscribe(t, caller.Id); Subscribe(t, callee.Id);
        var room = AddCall(t, caller.Id, callee.Id);
        var tr = new RecordingTransport();

        await Enqueue(t, "call", room.Id); await Run(t, tr);
        await Enqueue(t, "call", room.Id); await Run(t, tr); // same state again: no second ring
        Answer(t, room, callee.Id);
        await Enqueue(t, "call", room.Id); await Run(t, tr);
        await Enqueue(t, "call", room.Id); await Run(t, tr); // no second close

        Assert.Equal(2, tr.Sent.Count);
        var ring = tr.Sent[0];
        Assert.Equal(callee.Id, ring.Sub.UserId);
        Assert.Equal("call", ring.Payload.Kind);
        Assert.True(ring.Payload.RequireInteraction);
        Assert.True(ring.Ttl <= IPop.Infrastructure.Calls.CallService.RingTimeout);
        Assert.Equal((room.Id, callee.Id), PushDispatcher.ReadCallActionToken(Dp, ring.Payload.ActionToken!));
        var close = tr.Sent[1];
        Assert.Equal("close", close.Payload.Kind);
        Assert.Equal($"call:{room.Id}", close.Payload.Tag);
    }

    [Fact]
    public async Task Call_RingRetry_AfterAnswer_IsNeverSent()
    {
        using var t = new TestDb();
        var caller = t.AddUser("C"); var callee = t.AddUser("E");
        var s = Subscribe(t, callee.Id);
        var room = AddCall(t, caller.Id, callee.Id);
        var tr = new RecordingTransport();
        tr.Fail(s.Endpoint, PushSendOutcome.FromStatus(503), PushSendOutcome.Accepted);
        var now = DateTime.UtcNow.AddSeconds(1); // just after the enqueue below
        await Enqueue(t, "call", room.Id); await Run(t, tr, now);

        Answer(t, room, callee.Id);
        await Run(t, tr, now.AddSeconds(10)); // ring retry due: stale → suppressed

        var ring = Deliveries(t).Single(d => d.Kind == "call");
        Assert.Equal(PushDispatcher.Status.Suppressed, ring.Status);
        Assert.Equal("stale-call", ring.Code);
        Assert.Single(tr.Sent);
    }

    [Fact]
    public async Task Call_RingPastTimeout_IsNotQueued()
    {
        using var t = new TestDb();
        var caller = t.AddUser("C"); var callee = t.AddUser("E");
        Subscribe(t, callee.Id);
        var room = AddCall(t, caller.Id, callee.Id, ringAt: DateTime.UtcNow.AddMinutes(-5));

        var tr = await Push(t, "call", room.Id);

        Assert.Empty(tr.Sent);
    }

    [Fact]
    public void CallActionToken_RejectsTampering()
    {
        Assert.Null(PushDispatcher.ReadCallActionToken(Dp, "not-a-token"));
        Assert.Null(PushDispatcher.ReadCallActionToken(new EphemeralDataProtectionProvider(),
            Dp.CreateProtector(PushDispatcher.CallActionPurpose).ToTimeLimitedDataProtector()
                .Protect($"{Guid.NewGuid():N}|{Guid.NewGuid():N}", TimeSpan.FromMinutes(1))));
    }

    // ── Transport mapping ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(201, PushSendResult.Sent, null)]
    [InlineData(404, PushSendResult.Gone, "gone")]
    [InlineData(410, PushSendResult.Gone, "gone")]
    [InlineData(401, PushSendResult.AuthError, "auth-401")]
    [InlineData(403, PushSendResult.AuthError, "auth-403")]
    [InlineData(429, PushSendResult.Transient, "rate-limited")]
    [InlineData(500, PushSendResult.Transient, "server-error")]
    [InlineData(503, PushSendResult.Transient, "server-error")]
    [InlineData(400, PushSendResult.Rejected, "rejected-4xx")]
    [InlineData(413, PushSendResult.Rejected, "payload-too-large")]
    public void StatusCodes_MapToSanitizedOutcomes(int status, PushSendResult result, string? code)
    {
        var o = PushSendOutcome.FromStatus(status);
        Assert.Equal(result, o.Result);
        Assert.Equal(code, o.Code);
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/x", "fcm")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/x", "mozilla")]
    [InlineData("https://wns2-sg2p.notify.windows.com/w/?token=x", "wns")]
    [InlineData("https://web.push.apple.com/x", "apple")]
    [InlineData("https://evil.example/x", "other")]
    public void Provider_IsDerivedWithoutExposingTheEndpoint(string endpoint, string provider) =>
        Assert.Equal(provider, PushDispatcher.ProviderOf(endpoint));
}
