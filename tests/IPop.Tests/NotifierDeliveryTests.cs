using System.Text.Json;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications.Push;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// iPop Notifier (desktop app) delivery: chat toasts reach the app with the browser's audience rules, and a
/// Windows browser on the same account stays quiet while the app is online (no duplicates).
public class NotifierDeliveryTests
{
    private sealed class Transport : IWebPushTransport
    {
        public List<(PushSubscription Sub, PushPayload Payload)> Sent { get; } = [];
        public bool IsConfigured => true;
        public Task<PushSendOutcome> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct)
        {
            Sent.Add((sub, JsonSerializer.Deserialize<PushPayload>(json, PushDispatcher.Json)!));
            return Task.FromResult(PushSendOutcome.Accepted);
        }
    }

    private sealed class Sink : IDesktopToastSink
    {
        public List<(Guid User, PushPayload Payload)> Sent { get; } = [];
        public Task SendAsync(Guid userId, PushPayload payload, CancellationToken ct) { Sent.Add((userId, payload)); return Task.CompletedTask; }
    }

    private static PushSubscription Subscribe(TestDb t, Guid userId, string label)
    {
        var s = new PushSubscription
        {
            Id = Guid.NewGuid(), UserId = userId, Endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}",
            P256dh = "p", Auth = "a", Label = label, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        };
        t.Db.PushSubscriptions.Add(s);
        t.Db.SaveChanges();
        return s;
    }

    private static void Desktop(TestDb t, Guid userId, DateTime lastSeen)
    {
        t.Db.NotifierDevices.Add(new NotifierDevice
        {
            Id = Guid.NewGuid(), UserId = userId, DeviceKey = "pc-" + Guid.NewGuid().ToString("N")[..12], MachineName = "LOGISTIC33",
            FirstSeenAt = lastSeen, LastSeenAt = lastSeen,
        });
        t.Db.SaveChanges();
    }

    private static async Task<(Transport Tr, Sink Sink)> Run(TestDb t, string kind, Guid refId, IEnumerable<Guid>? users = null)
    {
        using (var db = t.Verify()) await new PushQueue().EnqueueAsync(db, kind, refId, default, users);
        var tr = new Transport();
        var sink = new Sink();
        await new PushDispatcher(t.Verify(), tr, new EphemeralDataProtectionProvider(), NullLogger<PushDispatcher>.Instance, sink)
            .ProcessAsync(DateTime.UtcNow, default);
        return (tr, sink);
    }

    [Fact]
    public async Task ChatMessage_ReachesDesktopApp_AndSkipsWindowsBrowserOnly()
    {
        using var t = new TestDb();
        var sender = t.AddUser("S"); var withApp = t.AddUser("A"); var noApp = t.AddUser("B");
        var room = t.AddRoom("group", "Tim IT");
        foreach (var u in new[] { sender, withApp, noApp }) t.AddMember(room.Id, u.Id);
        var appWindows = Subscribe(t, withApp.Id, "Edge · Windows");
        var appPhone = Subscribe(t, withApp.Id, "Chrome · Android");
        var otherWindows = Subscribe(t, noApp.Id, "Chrome · Windows");
        Desktop(t, withApp.Id, DateTime.UtcNow.AddSeconds(-30));
        var msg = t.AddMessage(room.Id, sender.Id, "Halo semua");

        var (tr, sink) = await Run(t, "chat", msg.Id);

        var toast = Assert.Single(sink.Sent);
        Assert.Equal(withApp.Id, toast.User);
        Assert.Equal("Halo semua", toast.Payload.Body);
        Assert.Equal($"/chat/{room.Id}", toast.Payload.Url);
        var endpoints = tr.Sent.Select(s => s.Sub.Id).ToHashSet();
        Assert.DoesNotContain(appWindows.Id, endpoints);  // the app shows it on that PC
        Assert.Contains(appPhone.Id, endpoints);          // the phone still rings
        Assert.Contains(otherWindows.Id, endpoints);      // no app → browser toast as before
    }

    [Fact]
    public async Task ChatMessage_StaleDesktop_FallsBackToBrowser()
    {
        using var t = new TestDb();
        var sender = t.AddUser("S"); var user = t.AddUser("A");
        var room = t.AddRoom();
        t.AddMember(room.Id, sender.Id); t.AddMember(room.Id, user.Id);
        var browser = Subscribe(t, user.Id, "Edge · Windows");
        Desktop(t, user.Id, DateTime.UtcNow - NotifierDevice.OnlineWindow - TimeSpan.FromMinutes(1));
        var msg = t.AddMessage(room.Id, sender.Id, "x");

        var (tr, sink) = await Run(t, "chat", msg.Id);

        Assert.Empty(sink.Sent);
        Assert.Contains(tr.Sent, s => s.Sub.Id == browser.Id);
    }

    [Fact]
    public async Task ChatMessage_MutedRoom_NoDesktopToastEither()
    {
        using var t = new TestDb();
        var sender = t.AddUser("S"); var user = t.AddUser("A");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, sender.Id); t.AddMember(room.Id, user.Id);
        t.AddSettings(user.Id, room.Id, s => s.IsMuted = true);
        Desktop(t, user.Id, DateTime.UtcNow);
        var msg = t.AddMessage(room.Id, sender.Id, "x");

        var (_, sink) = await Run(t, "chat", msg.Id);

        Assert.Empty(sink.Sent);
    }

    [Theory]
    [InlineData("Edge · Windows", true)]
    [InlineData("Chrome · Windows", true)]
    [InlineData("Chrome · Android", false)]
    [InlineData("Safari · iOS", false)]
    [InlineData("Browser", false)]
    [InlineData(null, false)]
    public void WindowsBrowserLabel(string? label, bool windows) =>
        Assert.Equal(windows, PushDispatcher.IsWindowsBrowser(label));
}
