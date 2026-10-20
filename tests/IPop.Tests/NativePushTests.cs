using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPop.Application.Calls;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications.Push;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Native Android app push (FCM): devices share the outbox with browsers, are routed to the FCM
/// transport, and replace the phone browser's Web Push so a tap never opens the browser.
public class NativePushTests
{
    private sealed class WebRecorder : IWebPushTransport
    {
        public List<PushSubscription> Sent { get; } = [];
        public bool IsConfigured { get; init; } = true;
        public Task<PushSendOutcome> SendAsync(PushSubscription sub, string json, TimeSpan ttl, bool urgent, string? topic, CancellationToken ct)
        { Sent.Add(sub); return Task.FromResult(PushSendOutcome.Accepted); }
    }

    private sealed class NativeRecorder : INativePushTransport
    {
        public List<(PushSubscription Sub, PushPayload Payload, bool Urgent)> Sent { get; } = [];
        public PushSendOutcome Next { get; set; } = PushSendOutcome.Accepted;
        public bool IsConfigured => true;
        public Task<PushSendOutcome> SendAsync(PushSubscription sub, PushPayload payload, TimeSpan ttl, bool urgent, CancellationToken ct)
        { Sent.Add((sub, payload, urgent)); return Task.FromResult(Next); }
    }

    private static PushSubscription Device(TestDb t, Guid userId, string endpoint, string label)
    {
        var s = new PushSubscription
        {
            Id = Guid.NewGuid(), UserId = userId, Endpoint = endpoint, P256dh = "p", Auth = "a", Label = label,
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        };
        t.Db.PushSubscriptions.Add(s);
        t.Db.SaveChanges();
        return s;
    }

    private static async Task Run(TestDb t, string kind, Guid refId, IWebPushTransport web, INativePushTransport? native)
    {
        using (var db = t.Verify()) await new PushQueue().EnqueueAsync(db, kind, refId, default);
        await new PushDispatcher(t.Verify(), web, new EphemeralDataProtectionProvider(), NullLogger<PushDispatcher>.Instance, null, native)
            .ProcessAsync(DateTime.UtcNow, default);
    }

    [Fact]
    public async Task Chat_goes_to_the_native_app_and_not_the_phone_browser_but_still_to_the_pc()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var room = t.AddRoom("dm", null);
        t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id);
        var app = Device(t, b.Id, NativePush.EndpointOf("tok-b"), NativePush.Label);
        Device(t, b.Id, "https://fcm.googleapis.com/fcm/send/phone", "Samsung Browser · Android");
        var pc = Device(t, b.Id, "https://fcm.googleapis.com/fcm/send/pc", "Chrome · Windows");
        var msg = t.AddMessage(room.Id, a.Id, "tes notif android");
        var web = new WebRecorder(); var native = new NativeRecorder();

        await Run(t, "chat", msg.Id, web, native);

        var sent = Assert.Single(native.Sent);
        Assert.Equal(app.Id, sent.Sub.Id);
        Assert.Equal("chat", sent.Payload.Kind);
        Assert.Equal($"/chat/{room.Id}", sent.Payload.Url);
        Assert.Equal(pc.Id, Assert.Single(web.Sent).Id);
    }

    [Fact]
    public async Task Without_the_native_app_the_phone_browser_still_gets_web_push()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var room = t.AddRoom("dm", null);
        t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id);
        var phone = Device(t, b.Id, "https://fcm.googleapis.com/fcm/send/phone", "Chrome · Android");
        var msg = t.AddMessage(room.Id, a.Id, "halo");
        var web = new WebRecorder(); var native = new NativeRecorder();

        await Run(t, "chat", msg.Id, web, native);

        Assert.Empty(native.Sent);
        Assert.Equal(phone.Id, Assert.Single(web.Sent).Id);
    }

    [Fact]
    public async Task Native_devices_get_nothing_while_fcm_is_off_and_web_push_still_works()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var room = t.AddRoom("dm", null);
        t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id);
        Device(t, b.Id, NativePush.EndpointOf("tok"), NativePush.Label);
        var pc = Device(t, b.Id, "https://fcm.googleapis.com/fcm/send/pc", "Chrome · Windows");
        var msg = t.AddMessage(room.Id, a.Id, "halo");
        var web = new WebRecorder();

        await Run(t, "chat", msg.Id, web, new DisabledNativePushTransport());

        Assert.Equal(pc.Id, Assert.Single(web.Sent).Id);
        Assert.DoesNotContain(t.Verify().PushDeliveries, d => d.Provider == "fcm-app");
    }

    [Fact]
    public async Task Fcm_only_server_still_delivers_to_the_app()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var room = t.AddRoom("dm", null);
        t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id);
        Device(t, b.Id, NativePush.EndpointOf("tok"), NativePush.Label);
        var msg = t.AddMessage(room.Id, a.Id, "halo");
        var native = new NativeRecorder();

        await Run(t, "chat", msg.Id, new WebRecorder { IsConfigured = false }, native);

        Assert.Single(native.Sent);
    }

    [Fact]
    public async Task Incoming_call_rings_the_app_with_a_decline_token_and_urgent_priority()
    {
        using var t = new TestDb();
        var caller = t.AddUser("C"); var callee = t.AddUser("E");
        Device(t, callee.Id, NativePush.EndpointOf("tok-e"), NativePush.Label);
        var now = DateTime.UtcNow;
        var room = new CallRoom
        {
            Id = Guid.NewGuid(), CreatorId = caller.Id, Kind = CallKind.User, Status = CallStatus.Ringing, StartedAt = now, CreatedAt = now, UpdatedAt = now,
            Participants =
            [
                new CallParticipant { Id = Guid.NewGuid(), UserId = caller.Id, Status = CallParticipantStatus.Accepted, JoinedAt = now },
                new CallParticipant { Id = Guid.NewGuid(), UserId = callee.Id, Status = CallParticipantStatus.Invited, JoinedAt = now },
            ],
        };
        t.Db.CallRooms.Add(room); t.Db.SaveChanges();
        var native = new NativeRecorder();

        await Run(t, "call", room.Id, new WebRecorder(), native);

        var sent = Assert.Single(native.Sent);
        Assert.True(sent.Urgent);
        var data = NativePush.DataOf(sent.Payload);
        Assert.Equal("call", data["kind"]);
        Assert.Equal("calls", data["channel"]);
        Assert.Equal(room.Id.ToString(), data["callId"]);
        Assert.False(string.IsNullOrEmpty(data["actionToken"]));
    }

    [Fact]
    public async Task Unregistered_token_removes_the_device()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var room = t.AddRoom("dm", null);
        t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id);
        Device(t, b.Id, NativePush.EndpointOf("dead"), NativePush.Label);
        var msg = t.AddMessage(room.Id, a.Id, "halo");

        await Run(t, "chat", msg.Id, new WebRecorder(), new NativeRecorder { Next = new(PushSendResult.Gone, "gone") });

        Assert.Empty(t.Verify().PushSubscriptions);
    }

    [Theory]
    [InlineData(404, "{\"error\":{\"status\":\"NOT_FOUND\",\"details\":[{\"errorCode\":\"UNREGISTERED\"}]}}", PushSendResult.Gone)]
    [InlineData(400, "{\"error\":{\"message\":\"The registration token is not a valid FCM registration token\"}}", PushSendResult.Gone)]
    [InlineData(403, "{\"error\":{\"details\":[{\"errorCode\":\"SENDER_ID_MISMATCH\"}]}}", PushSendResult.Gone)]
    [InlineData(401, "{}", PushSendResult.AuthError)]
    [InlineData(429, "{}", PushSendResult.Transient)]
    [InlineData(503, "{}", PushSendResult.Transient)]
    [InlineData(400, "{\"error\":{\"message\":\"Invalid JSON payload\"}}", PushSendResult.Rejected)]
    public void Fcm_errors_map_to_outbox_outcomes(int status, string body, PushSendResult expected)
        => Assert.Equal(expected, FcmTransport.OutcomeOf(status, body, null).Result);

    [Fact]
    public void Data_payload_is_all_strings_with_channel_and_room()
    {
        var room = Guid.NewGuid();
        var d = NativePush.DataOf(new PushPayload("chat", "Rina", "Halo", $"/chat/{room}", $"chat:{room}", DeliveryId: "abc"));
        Assert.Equal("chat", d["channel"]);
        Assert.Equal(room.ToString(), d["roomId"]);
        Assert.Equal("abc", d["deliveryId"]);
        Assert.Equal("mail", NativePush.ChannelOf(new PushPayload("notif", "t", "b", "/mail/1", "notif:1")));
        Assert.Equal("meetings", NativePush.ChannelOf(new PushPayload("notif", "t", "b", "/calendar?event=1", "notif:2")));
        Assert.Equal("tickets", NativePush.ChannelOf(new PushPayload("notif", "t", "b", "/tickets/9", "notif:3")));
        Assert.Equal("system", NativePush.ChannelOf(new PushPayload("notif", "t", "b", null, "notif:4")));
    }

    [Fact]
    public void Service_account_jwt_is_rs256_signed_and_scoped_to_fcm()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var jwt = FcmTransport.SignedJwt("svc@proj.iam.gserviceaccount.com", pem, "https://oauth2.googleapis.com/token", DateTimeOffset.UtcNow);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] B64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
        var claims = JsonDocument.Parse(B64(parts[1])).RootElement;
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", claims.GetProperty("scope").GetString());
        Assert.Equal("svc@proj.iam.gserviceaccount.com", claims.GetProperty("iss").GetString());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task Fcm_transport_posts_a_data_message_with_high_priority_for_chat()
    {
        using var rsa = RSA.Create(2048);
        var dir = Directory.CreateTempSubdirectory("ipop-fcm-").FullName;
        var sa = Path.Combine(dir, "sa.json");
        await File.WriteAllTextAsync(sa, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["client_email"] = "svc@proj.iam.gserviceaccount.com", ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["token_uri"] = "https://oauth2.googleapis.com/token",
        }));
        var handler = new StubHandler();
        var opts = new FcmOptions { ProjectId = "proj", ServiceAccountPath = sa, ApplicationId = "1:2:android:3", ApiKey = "k", SenderId = "2" };
        using var fcm = new FcmTransport(opts, NullLogger.Instance, handler);
        var sub = new PushSubscription { Endpoint = NativePush.EndpointOf("device-token"), P256dh = "-", Auth = "-" };

        var outcome = await fcm.SendAsync(sub, new PushPayload("chat", "Rina", "Halo", "/chat/x", "chat:x"), TimeSpan.FromHours(1), false, default);

        Assert.Equal(PushSendResult.Sent, outcome.Result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://fcm.googleapis.com/v1/projects/proj/messages:send", handler.Requests[1].Url);
        Assert.Equal("Bearer test-access", handler.Requests[1].Auth);
        var msg = JsonDocument.Parse(handler.Requests[1].Body).RootElement.GetProperty("message");
        Assert.Equal("device-token", msg.GetProperty("token").GetString());
        Assert.Equal("HIGH", msg.GetProperty("android").GetProperty("priority").GetString());
        Assert.Equal("3600s", msg.GetProperty("android").GetProperty("ttl").GetString());
        Assert.Equal("Halo", msg.GetProperty("data").GetProperty("body").GetString());
        Assert.False(msg.TryGetProperty("notification", out _));

        // The OAuth token is cached: a second send makes one request, not two.
        await fcm.SendAsync(sub, new PushPayload("notif", "t", "b", "/mail/1", "notif:1"), TimeSpan.FromHours(1), false, default);
        Assert.Equal(3, handler.Requests.Count);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<(string Url, string? Auth, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            var json = request.RequestUri!.Host == "oauth2.googleapis.com"
                ? "{\"access_token\":\"test-access\",\"expires_in\":3600}"
                : "{\"name\":\"projects/proj/messages/1\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
