using System.Text.Json;
using IPop.Application.Calls;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications.Push;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Web Push fan-out: who gets a Windows toast, what it says, and cleanup of dead subscriptions.
public class PushDispatcherTests
{
    private sealed class RecordingTransport : IWebPushTransport
    {
        public List<(PushSubscription Sub, PushPayload Payload, bool Urgent)> Sent { get; } = [];
        public HashSet<string> GoneEndpoints { get; } = [];
        public bool IsConfigured => true;

        public Task<PushSendResult> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct)
        {
            if (GoneEndpoints.Contains(sub.Endpoint)) return Task.FromResult(PushSendResult.Gone);
            Sent.Add((sub, JsonSerializer.Deserialize<PushPayload>(json, PushDispatcher.Json)!, urgent));
            return Task.FromResult(PushSendResult.Sent);
        }

        public IEnumerable<Guid> Recipients => Sent.Select(s => s.Sub.UserId);
    }

    private static readonly IDataProtectionProvider Dp = new EphemeralDataProtectionProvider();

    private static PushDispatcher Dispatcher(TestDb t, RecordingTransport tr) =>
        new(t.Verify(), tr, Dp, NullLogger<PushDispatcher>.Instance);

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

    // ── Chat messages ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatMessage_ToastsOtherMembers_NotTheSender()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B"); var c = t.AddUser("C");
        var room = t.AddRoom("group", "Tim IT");
        foreach (var u in new[] { a, b, c }) { t.AddMember(room.Id, u.Id); Subscribe(t, u.Id); }
        var msg = t.AddMessage(room.Id, a.Id, "Halo semua");
        var tr = new RecordingTransport();

        await Dispatcher(t, tr).NotifyChatMessageAsync(msg.Id, default);

        Assert.Equal(new[] { b.Id, c.Id }.OrderBy(x => x), tr.Recipients.OrderBy(x => x));
        var p = tr.Sent[0].Payload;
        Assert.Equal("chat", p.Kind);
        Assert.Equal("User A · Tim IT", p.Title);
        Assert.Equal("Halo semua", p.Body);
        Assert.Equal($"chat:{room.Id}", p.Tag);
        Assert.Equal($"/chat/{room.Id}", p.Url);
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
        var tr = new RecordingTransport();

        await Dispatcher(t, tr).NotifyChatMessageAsync(msg.Id, default);

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
        var tr = new RecordingTransport();

        await Dispatcher(t, tr).NotifyChatMessageAsync(msg.Id, default);

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
        var tr = new RecordingTransport();

        await Dispatcher(t, tr).NotifyChatMessageAsync(msg.Id, default);

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
        var tr = new RecordingTransport();

        await Dispatcher(t, tr).NotifyChatMessageAsync(msg.Id, default);

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
        var tr = new RecordingTransport();

        await Dispatcher(t, tr).NotifyChatMessageAsync(msg.Id, default);

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

    // ── Delivery mechanics ─────────────────────────────────────────────────────

    [Fact]
    public async Task Send_ReachesEveryBrowser_AndDeletesGoneSubscriptions()
    {
        using var t = new TestDb();
        var u = t.AddUser("A");
        var live = Subscribe(t, u.Id);
        var dead = Subscribe(t, u.Id);
        var tr = new RecordingTransport();
        tr.GoneEndpoints.Add(dead.Endpoint);

        await Dispatcher(t, tr).SendAsync([u.Id], new PushPayload("notif", "T", "B", "/", "x"), urgent: false, default);

        Assert.Equal(live.Endpoint, Assert.Single(tr.Sent).Sub.Endpoint);
        var left = await t.Verify().PushSubscriptions.Select(s => s.Id).ToListAsync();
        Assert.Equal(new[] { live.Id }, left);
    }

    [Fact]
    public async Task Send_StampsEachSubscriptionsApiBase()
    {
        using var t = new TestDb();
        var u = t.AddUser("A");
        var s = Subscribe(t, u.Id);
        s.ApiBase = "https://ipop.santos.co.id";
        t.Db.SaveChanges();
        var tr = new RecordingTransport();

        await Dispatcher(t, tr).SendAsync([u.Id], new PushPayload("notif", "T", "B", "/", "x"), urgent: false, default);

        Assert.Equal("https://ipop.santos.co.id", Assert.Single(tr.Sent).Payload.ApiBase);
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
    public async Task Request_ChatMention_SharesTheRoomTag()
    {
        using var t = new TestDb();
        var u = t.AddUser("A"); Subscribe(t, u.Id);
        var room = Guid.NewGuid();
        var tr = new RecordingTransport();

        await Dispatcher(t, tr).NotifyRequestAsync([u.Id], "chat.mention", "high", "Budi menyebut Anda · G", "cek ini",
            $"/chat/{room}?jump={Guid.NewGuid()}", Guid.NewGuid(), default);

        Assert.Equal($"chat:{room}", Assert.Single(tr.Sent).Payload.Tag);
    }

    // ── Calls ──────────────────────────────────────────────────────────────────

    private static CallRoomDto Call(Guid id, Guid caller, Guid callee, string status, string calleeStatus) =>
        new(id, CallKind.User, status, null, caller, DateTime.UtcNow, null, null, "hd",
            [new CallParticipantDto(caller, "C", "Caller", CallParticipantStatus.Accepted, false, false, null),
             new CallParticipantDto(callee, "E", "Callee", calleeStatus, false, false, null)],
            null, null);

    [Fact]
    public async Task Call_RingsOnce_ThenClosesWhenAnswered()
    {
        using var t = new TestDb();
        var caller = t.AddUser("C"); var callee = t.AddUser("E");
        Subscribe(t, caller.Id); Subscribe(t, callee.Id);
        var id = Guid.NewGuid();
        var tr = new RecordingTransport();
        var d = Dispatcher(t, tr);

        await d.SyncCallAsync(Call(id, caller.Id, callee.Id, CallStatus.Ringing, CallParticipantStatus.Invited), default);
        await d.SyncCallAsync(Call(id, caller.Id, callee.Id, CallStatus.Ringing, CallParticipantStatus.Invited), default);
        await d.SyncCallAsync(Call(id, caller.Id, callee.Id, CallStatus.Active, CallParticipantStatus.Accepted), default);

        Assert.Equal(2, tr.Sent.Count);
        var ring = tr.Sent[0];
        Assert.Equal(callee.Id, ring.Sub.UserId);
        Assert.Equal("call", ring.Payload.Kind);
        Assert.True(ring.Payload.RequireInteraction);
        Assert.Equal((id, callee.Id), PushDispatcher.ReadCallActionToken(Dp, ring.Payload.ActionToken!));
        var close = tr.Sent[1];
        Assert.Equal("close", close.Payload.Kind);
        Assert.Equal($"call:{id}", close.Payload.Tag);
    }

    [Fact]
    public void CallActionToken_RejectsTampering()
    {
        Assert.Null(PushDispatcher.ReadCallActionToken(Dp, "not-a-token"));
        Assert.Null(PushDispatcher.ReadCallActionToken(new EphemeralDataProtectionProvider(),
            Dp.CreateProtector(PushDispatcher.CallActionPurpose).ToTimeLimitedDataProtector()
                .Protect($"{Guid.NewGuid():N}|{Guid.NewGuid():N}", TimeSpan.FromMinutes(1))));
    }
}
