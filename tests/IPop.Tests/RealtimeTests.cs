using IPop.Api.Presence;
using IPop.Api.Realtime;
using IPop.Application.Realtime;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace IPop.Tests;

public class RealtimeTests
{
    /// Records every send with the target it went to ("all", "group:x", "groups:a,b").
    private sealed class FakeHub : IHubContext<AppHub>
    {
        public List<(string Target, string Method, object?[] Args)> Sent { get; } = [];
        public IHubClients Clients => new FakeClients(this);
        public IGroupManager Groups => throw new NotSupportedException();

        private sealed class FakeClients(FakeHub hub) : IHubClients
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

        private sealed class Proxy(FakeHub hub, string target) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            { hub.Sent.Add((target, method, args)); return Task.CompletedTask; }
        }
    }

    [Fact]
    public async Task ToUser_TargetsUserGroup()
    {
        var hub = new FakeHub();
        await new HubRealtime(hub).ToUserAsync("90001", RealtimeEvents.BadgeChanged, new RealtimeArgs("chat", 3));

        var s = Assert.Single(hub.Sent);
        Assert.Equal("group:user:90001", s.Target);
        Assert.Equal("badgeChanged", s.Method);
        Assert.Equal(["chat", 3], s.Args);
    }

    [Fact]
    public async Task ToUsers_DedupesIntoOneMultiGroupSend()
    {
        var hub = new FakeHub();
        await new HubRealtime(hub).ToUsersAsync(["a", "b", "a"], "x");
        Assert.Equal("groups:user:a,user:b", Assert.Single(hub.Sent).Target);
    }

    [Fact]
    public async Task SinglePayload_IsOneArgument()
    {
        var hub = new FakeHub();
        var payload = new { id = 1 };
        await new HubRealtime(hub).ToGroupAsync("conv:1", "messageCreated", payload);
        Assert.Same(payload, Assert.Single(Assert.Single(hub.Sent).Args));
    }

    [Fact]
    public async Task ExistingNotifiers_KeepEventNamesAndArity()
    {
        var hub = new FakeHub();
        var rt = new HubRealtime(hub);

        await new DirectoryChangeNotifier(rt).NotifyAsync();
        await new AuditChangeNotifier(rt).NotifyAsync();
        await new PresenceBroadcaster(rt).BroadcastAsync("90001", "busy");

        Assert.Equal(
            [("all", "directoryChanged", 0), ("all", "auditChanged", 0), ("all", "presenceChanged", 2)],
            hub.Sent.Select(s => (s.Target, s.Method, s.Args.Length)).ToList());
        Assert.Equal(["90001", "busy"], hub.Sent[2].Args);
    }
}
