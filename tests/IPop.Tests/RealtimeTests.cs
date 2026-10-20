using IPop.Api.Presence;
using IPop.Api.Realtime;
using IPop.Application.Realtime;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace IPop.Tests;

public class RealtimeTests
{
    /// Records every send with the target it went to ("all", "group:x", "groups:a,b").
    private class FakeHub<THub> : IHubContext<THub> where THub : Hub
    {
        public List<(string Target, string Method, object?[] Args)> Sent { get; } = [];
        public IHubClients Clients => new FakeClients(this);
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class FakeClients(FakeHub<THub> hub) : IHubClients
        {
            private IClientProxy P(string target) => new Proxy(hub, target);
            public IClientProxy All => P("all");
            public IClientProxy Group(string groupName) => P($"group:{groupName}");
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => P($"groups:{string.Join(",", groupNames)}");
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy Client(string connectionId) => throw new NotSupportedException();
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IClientProxy User(string userId) => throw new NotSupportedException();
            public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
        }

        private sealed class Proxy(FakeHub<THub> hub, string target) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            { hub.Sent.Add((target, method, args)); return Task.CompletedTask; }
        }
    }
    private sealed class FakeHub : FakeHub<AppHub>;
    private sealed class FakeNotifHub : FakeHub<IPop.Api.Notifications.NotificationHub>;
    private static HubRealtime Rt(FakeHub hub, FakeNotifHub? notif = null) => new(hub, notif ?? new FakeNotifHub());

    [Fact]
    public async Task ToUser_TargetsUserGroup()
    {
        var hub = new FakeHub();
        await Rt(hub).ToUserAsync("90001", RealtimeEvents.BadgeChanged, new RealtimeArgs("chat", 3));

        var s = Assert.Single(hub.Sent);
        Assert.Equal("group:user:90001", s.Target);
        Assert.Equal("badgeChanged", s.Method);
        Assert.Equal(["chat", 3], s.Args);
    }

    [Fact]
    public async Task ToUsers_DedupesIntoOneMultiGroupSend()
    {
        var hub = new FakeHub();
        await Rt(hub).ToUsersAsync(["a", "b", "a"], "x");
        Assert.Equal("groups:user:a,user:b", Assert.Single(hub.Sent).Target);
    }

    [Fact]
    public async Task SinglePayload_IsOneArgument()
    {
        var hub = new FakeHub();
        var payload = new { id = 1 };
        await Rt(hub).ToGroupAsync("conv:1", "messageCreated", payload);
        Assert.Same(payload, Assert.Single(Assert.Single(hub.Sent).Args));
    }

    [Fact]
    public async Task ExistingNotifiers_KeepEventNamesAndArity()
    {
        var hub = new FakeHub();
        var rt = Rt(hub);

        await new DirectoryChangeNotifier(rt).NotifyAsync();
        await new AuditChangeNotifier(rt).NotifyAsync();
        await new PresenceBroadcaster(rt).BroadcastAsync("90001", "busy");

        Assert.Equal(
            [("all", "directoryChanged", 0), ("all", "auditChanged", 0), ("all", "presenceChanged", 2)],
            hub.Sent.Select(s => (s.Target, s.Method, s.Args.Length)).ToList());
        Assert.Equal(["90001", "busy"], hub.Sent[2].Args);
    }

    /// The iPop Notifier only joins /hubs/notification: the bell refresh must reach it there, other events must not.
    [Fact]
    public async Task NotificationsChanged_IsMirroredToTheNotificationHub()
    {
        var hub = new FakeHub(); var notif = new FakeNotifHub(); var rt = Rt(hub, notif);
        await rt.ToUserAsync("90001", RealtimeEvents.NotificationsChanged);
        await rt.ToUsersAsync(["a", "b"], RealtimeEvents.NotificationsChanged);
        await rt.ToUserAsync("90001", RealtimeEvents.BadgeChanged, new RealtimeArgs("chat", 3));
        Assert.Equal(3, hub.Sent.Count);
        Assert.Equal([("group:user:90001", "notificationsChanged"), ("groups:user:a,user:b", "notificationsChanged")],
            notif.Sent.Select(s => (s.Target, s.Method)).ToList());
    }
}
