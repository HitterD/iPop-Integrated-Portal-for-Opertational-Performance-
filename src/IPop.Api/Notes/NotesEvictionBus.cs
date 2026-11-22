using IPop.Application.Notes;
using IPop.Application.Rbac;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace IPop.Api.Notes;

/// Relays NotesHub eviction events across API replicas via Redis Pub/Sub so revoking access on one pod evicts open tabs on every pod.
public sealed class NotesEvictionBus(IConfiguration config, IServiceScopeFactory scopes, ILogger<NotesEvictionBus> log) : BackgroundService
{
    private static readonly RedisChannel Channel = RedisChannel.Literal("ipop-notes:evict");
    private readonly string _nodeId = Guid.NewGuid().ToString("N");
    private ConnectionMultiplexer? _redis;

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
            NotesHub.PublishEviction = async (pageId, _) =>
            {
                try { await sub.PublishAsync(Channel, $"{_nodeId}:{pageId:D}", CommandFlags.FireAndForget); }
                catch (Exception ex) { log.LogWarning(ex, "Notes eviction publish failed for {PageId}", pageId); }
            };
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { log.LogWarning(ex, "Notes eviction Redis bus unavailable; local eviction only"); }
        finally
        {
            NotesHub.PublishEviction = null;
            if (_redis is not null) await _redis.DisposeAsync();
        }
    }

    private async Task HandleAsync(string payload, CancellationToken ct)
    {
        var sep = payload.IndexOf(':');
        if (sep <= 0 || payload[..sep] == _nodeId) return;
        if (!Guid.TryParse(payload.AsSpan(sep + 1), out var pageId)) return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<NotesHub>>();
            var notes = scope.ServiceProvider.GetRequiredService<INoteService>();
            var perms = scope.ServiceProvider.GetService<IPermissionEvaluator>();
            await NotesHub.EvictLostAccessAsync(hub, notes, pageId, ct, perms);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Remote Notes eviction failed for {PageId}", pageId);
        }
    }
}
