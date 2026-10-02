using System.Security.Claims;
using IPop.Api.Chat;
using IPop.Application.Chat;
using IPop.Application.Rbac;
using IPop.Application.Realtime;
using IPop.Infrastructure.Chat;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace IPop.Tests;

/// The SendMessage hub method answers every call with a result echoing the request's room and
/// idempotency key, so the client can settle exactly that message. Failures are no longer a
/// bare "error" event the client could only pin on the oldest pending bubble.
public class ChatHubSendResultTests
{
    private sealed class Ctx(ClaimsPrincipal? user) : HubCallerContext
    {
        public override string ConnectionId => "c1";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => default;
        public override void Abort() { }
    }

    private sealed class Proxy(List<string> sent) : ISingleClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) { sent.Add(method); return Task.CompletedTask; }
        public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CallerClients(List<string> sent) : IHubCallerClients
    {
        public ISingleClientProxy Caller => new Proxy(sent);
        IClientProxy IHubCallerClients<IClientProxy>.Caller => new Proxy(sent);
        IClientProxy IHubClients<IClientProxy>.Client(string id) => new Proxy(sent);
        public IClientProxy Others => new Proxy(sent);
        public IClientProxy All => new Proxy(sent);
        public IClientProxy AllExcept(IReadOnlyList<string> ids) => new Proxy(sent);
        public ISingleClientProxy Client(string id) => new Proxy(sent);
        public IClientProxy Clients(IReadOnlyList<string> ids) => new Proxy(sent);
        public IClientProxy Group(string g) => new Proxy(sent);
        public IClientProxy GroupExcept(string g, IReadOnlyList<string> ids) => new Proxy(sent);
        public IClientProxy Groups(IReadOnlyList<string> g) => new Proxy(sent);
        public IClientProxy OthersInGroup(string g) => new Proxy(sent);
        public IClientProxy User(string u) => new Proxy(sent);
        public IClientProxy Users(IReadOnlyList<string> u) => new Proxy(sent);
    }

    private sealed class Perms(bool allow) : IPermissionEvaluator
    {
        public Task<bool> IsAllowedAsync(Guid userId, string code, string? dept, CancellationToken ct = default) => Task.FromResult(allow);
    }

    public class Notifier : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? m, object?[]? a) => Task.CompletedTask;
    }

    private sealed class Realtime : IRealtime
    {
        public Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ToGroupAsync(string group, string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (ChatHub Hub, List<string> Sent) Hub(TestDb t, ClaimsPrincipal? user, bool allow = true)
    {
        var sent = new List<string>();
        var notifier = System.Reflection.DispatchProxy.Create<IChatNotifier, Notifier>();
        var hub = new ChatHub(new ChatService(t.Db), notifier, t.Db, new Perms(allow), new Realtime())
        {
            Context = new Ctx(user),
            Clients = new CallerClients(sent),
        };
        return (hub, sent);
    }

    private static ClaimsPrincipal Principal(Guid id, string nik) =>
        new(new ClaimsIdentity([new Claim("sub", id.ToString()), new Claim("nik", nik)], "test"));

    [Fact]
    public async Task Success_returns_message_id_with_the_request_key()
    {
        using var t = new TestDb();
        var me = t.AddUser("S1");
        var room = t.AddRoom();
        t.AddMember(room.Id, me.Id);
        var (hub, sent) = Hub(t, Principal(me.Id, me.Nik));
        var key = Guid.NewGuid();

        var r = await hub.SendMessage(new SendMessageHubRequest(room.Id, "hi", key, null, "text"));

        Assert.True(r.Success);
        Assert.Equal((room.Id, key), (r.RoomId, r.IdempotencyKey));
        Assert.NotNull(r.MessageId);
        Assert.Contains("messageAck", sent); // older clients still get the ack event
    }

    [Fact]
    public async Task Same_key_retry_returns_the_same_message_instead_of_a_duplicate()
    {
        using var t = new TestDb();
        var me = t.AddUser("S2");
        var room = t.AddRoom();
        t.AddMember(room.Id, me.Id);
        var (hub, _) = Hub(t, Principal(me.Id, me.Nik));
        var req = new SendMessageHubRequest(room.Id, "once", Guid.NewGuid(), null, "text");

        var first = await hub.SendMessage(req);
        var again = await hub.SendMessage(req);

        Assert.Equal(first.MessageId, again.MessageId);
        Assert.Equal(1, t.Verify().ChatMessages.Count(m => m.RoomId == room.Id));
    }

    [Fact]
    public async Task Every_failure_path_returns_a_correlated_error_and_no_error_event()
    {
        using var t = new TestDb();
        var me = t.AddUser("S3");
        var room = t.AddRoom();
        var key = Guid.NewGuid();

        async Task Expect(string code, ClaimsPrincipal? user, SendMessageHubRequest req, bool allow = true)
        {
            var (hub, sent) = Hub(t, user, allow);
            var r = await hub.SendMessage(req);
            Assert.False(r.Success);
            Assert.Equal(code, r.Error);
            Assert.Equal((req.RoomId, req.IdempotencyKey), (r.RoomId, r.IdempotencyKey));
            Assert.Empty(sent);
        }

        var ok = new SendMessageHubRequest(room.Id, "hi", key, null, "text");
        await Expect("unauthorized", new ClaimsPrincipal(new ClaimsIdentity()), ok);
        await Expect("forbidden", Principal(me.Id, me.Nik), ok, allow: false);
        await Expect("content_required", Principal(me.Id, me.Nik), ok with { Content = null! });
        await Expect("payload_too_large", Principal(me.Id, me.Nik), ok with { Content = new string('x', 4001) });
        await Expect("room_not_found", Principal(me.Id, me.Nik), ok); // not a member
    }
}
