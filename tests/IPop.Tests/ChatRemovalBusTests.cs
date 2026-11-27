using IPop.Api.Chat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Removing a member pulls their local sockets out of the room group; with no Redis the bus is a
/// clean no-op (single node), and with Redis the publish hook is what reaches the other replicas.
public class ChatRemovalBusTests
{
    private sealed class FakeGroups : IGroupManager
    {
        public readonly List<(string Conn, string Group)> Removed = new();
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
        { lock (Removed) Removed.Add((connectionId, groupName)); return Task.CompletedTask; }
    }

    private sealed class FakeHub(FakeGroups groups) : IHubContext<ChatHub>
    {
        public IHubClients Clients => throw new NotSupportedException();
        public IGroupManager Groups { get; } = groups;
    }

    [Fact]
    public async Task Removal_pulls_every_local_socket_of_the_user_out_of_the_room_group()
    {
        var user = Guid.NewGuid(); var room = Guid.NewGuid();
        ChatConnections.Add(user, "c1"); ChatConnections.Add(user, "c2");
        var groups = new FakeGroups();

        await ChatNotifier.RemoveLocalConnectionsAsync(new FakeHub(groups), user, room, default);

        Assert.Equal(new[] { "c1", "c2" }, groups.Removed.Select(r => r.Conn).Order());
        Assert.All(groups.Removed, r => Assert.Equal($"chat:{room}", r.Group));
        ChatConnections.Remove(user, "c1"); ChatConnections.Remove(user, "c2");
    }

    [Fact]
    public async Task Without_redis_the_bus_starts_and_stops_cleanly_and_publishes_nothing()
    {
        var bus = new ChatRemovalBus(new ConfigurationBuilder().Build(), new FakeHub(new FakeGroups()), NullLogger<ChatRemovalBus>.Instance);

        await bus.StartAsync(default);
        await bus.StopAsync(default);

        Assert.Null(ChatRemovalBus.Publish);
    }
}
