using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace IPop.Api.Chat;

/// Relays "user X left room Y" across API replicas via Redis Pub/Sub, so the node that handled the
/// removal and every other node each pull that user's sockets out of the room group. Without it,
/// ChatConnections (per process) only reaches the sockets held by the node that took the request.
/// Same pattern as NotesEvictionBus; with no Redis it degrades to local-only.
public sealed class ChatRemovalBus(IConfiguration config, IHubContext<ChatHub> hub, ILogger<ChatRemovalBus> log) : BackgroundService
{
    private static readonly RedisChannel Channel = RedisChannel.Literal("ipop-chat:removed");
    private readonly string _nodeId = Guid.NewGuid().ToString("N");
    private ConnectionMultiplexer? _redis;

    /// Installed while the bus runs; null = single node.
    public static Func<Guid, Guid, Task>? Publish { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cs = config.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(cs)) return;
        try
        {
            var opts = ConfigurationOptions.Parse(cs);
            opts.AbortOnConnectFail = false;
            _redis = await ConnectionMultiplexer.ConnectAsync(opts);
            var sub = _redis.GetSubscriber();
            await sub.SubscribeAsync(Channel, (ch, value) => { _ = HandleAsync(value.ToString(), stoppingToken); });
            Publish = async (userId, roomId) =>
            {
                try { await sub.PublishAsync(Channel, $"{_nodeId}|{userId:D}|{roomId:D}", CommandFlags.FireAndForget); }
                catch (Exception ex) { log.LogWarning(ex, "Chat removal publish failed for room {RoomId}", roomId); }
            };
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { log.LogWarning(ex, "Chat removal Redis bus unavailable; local removal only"); }
        finally
        {
            Publish = null;
            if (_redis is not null) await _redis.DisposeAsync();
        }
    }

    private async Task HandleAsync(string payload, CancellationToken ct)
    {
        var parts = payload.Split('|');
        if (parts.Length != 3 || parts[0] == _nodeId) return;
        if (!Guid.TryParse(parts[1], out var userId) || !Guid.TryParse(parts[2], out var roomId)) return;
        try { await ChatNotifier.RemoveLocalConnectionsAsync(hub, userId, roomId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Remote chat removal failed for room {RoomId}", roomId);
        }
    }
}
