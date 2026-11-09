using IPop.Application.Calls;
using IPop.Infrastructure.Calls;
using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Calls;

/// Broadcasts the end of calls that closed without a user action (timeout, dropped tab).
public static class CallBroadcast
{
    public static async Task EndedAsync(IHubContext<CallHub> hub, IEnumerable<CallRoomDto> rooms, CancellationToken ct,
        IPop.Infrastructure.Persistence.IPopDbContext? db = null, IPop.Infrastructure.Notifications.Push.PushQueue? push = null)
    {
        foreach (var room in rooms)
        {
            if (db is not null) await IPop.Api.Endpoints.CallEndpoints.SyncCallToastAsync(push, db, room, ct);
            // Group calls: every group member's banner must learn the call changed/ended.
            if (db is not null && room.ChatRoomId is not null)
            {
                await IPop.Api.Endpoints.CallEndpoints.BroadcastGroupAsync(hub, db, room, ct);
                continue;
            }
            foreach (var p in room.Participants)
                await hub.Clients.Group($"user:{p.UserId}").SendAsync("callStateChanged", room, ct);
        }
    }
}

/// Counts open /hubs/calls connections per user. When the last one closes (refresh, Wi-Fi blip,
/// laptop asleep) the user keeps their place in their calls for the grace period: others see the
/// call go on while the tab rejoins it. Coming back inside the grace cancels the pending end;
/// staying away ends their calls (a 1:1 for both sides; a group/ad-hoc call marks them left).
public class CallConnections
{
    /// Default grace; "Calls:DisconnectGraceSeconds" overrides it.
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(60);

    private readonly TimeSpan grace;
    private readonly Func<Guid, Task> endCalls;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly object gate = new();
    private readonly Dictionary<Guid, int> open = [];
    private readonly Dictionary<Guid, CancellationTokenSource> pending = [];

    public CallConnections(IServiceScopeFactory scopes, IHubContext<CallHub> hub, ILogger<CallConnections> log, IConfiguration config)
        : this(GraceFrom(config), userId => EndCallsAsync(scopes, hub, log, userId, GraceFrom(config))) { }

    internal CallConnections(TimeSpan grace, Func<Guid, Task> endCalls,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.grace = grace;
        this.endCalls = endCalls;
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public TimeSpan Grace => grace;

    private static TimeSpan GraceFrom(IConfiguration config)
        => config.GetValue<int?>("Calls:DisconnectGraceSeconds") is int s and > 0 ? TimeSpan.FromSeconds(s) : DefaultGrace;

    public void Connected(Guid userId)
    {
        lock (gate)
        {
            open[userId] = open.GetValueOrDefault(userId) + 1;
            // Back inside the grace (refresh / auto-reconnect): keep the call.
            if (pending.Remove(userId, out var cts)) { cts.Cancel(); cts.Dispose(); }
        }
    }

    public void Disconnected(Guid userId)
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            var left = open.GetValueOrDefault(userId) - 1;
            if (left > 0) { open[userId] = left; return; }
            open.Remove(userId);
            if (pending.Remove(userId, out var old)) { old.Cancel(); old.Dispose(); }
            cts = pending[userId] = new CancellationTokenSource();
        }
        _ = EndAfterGraceAsync(userId, cts);
    }

    public bool IsConnected(Guid userId)
    {
        lock (gate) return open.ContainsKey(userId);
    }

    private async Task EndAfterGraceAsync(Guid userId, CancellationTokenSource cts)
    {
        try { await delay(grace, cts.Token); }
        catch (OperationCanceledException) { return; }
        lock (gate)
        {
            if (!pending.TryGetValue(userId, out var current) || current != cts) return;
            pending.Remove(userId);
            cts.Dispose();
            if (open.ContainsKey(userId)) return;
        }
        await endCalls(userId);
    }

    private static async Task EndCallsAsync(IServiceScopeFactory scopes, IHubContext<CallHub> hub, ILogger log, Guid userId, TimeSpan grace)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var calls = scope.ServiceProvider.GetRequiredService<ICallService>();
            // These counts are per API replica. With several replicas the user's call tab may live on
            // another one (or have reconnected there): a heartbeat inside the grace proves it, keep that call.
            var ended = await calls.EndForUserAsync(userId, CancellationToken.None, unlessSeenAfter: DateTime.UtcNow - grace);
            await CallBroadcast.EndedAsync(hub, ended, CancellationToken.None,
                scope.ServiceProvider.GetRequiredService<IPop.Infrastructure.Persistence.IPopDbContext>(),
                scope.ServiceProvider.GetService<IPop.Infrastructure.Notifications.Push.PushQueue>());
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Ending calls after disconnect failed for {UserId}.", userId);
        }
    }
}

/// Unanswered rings become "missed" after <see cref="CallService.RingTimeout"/>. Safe on several API
/// replicas: each room is closed with a concurrency check, so only one replica records and broadcasts it.
public class CallRingTimeoutWorker(IServiceScopeFactory scopes, IHubContext<CallHub> hub, ILogger<CallRingTimeoutWorker> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                using var scope = scopes.CreateScope();
                var calls = scope.ServiceProvider.GetRequiredService<ICallService>();
                var expired = await calls.ExpireRingingAsync(DateTime.UtcNow - CallService.RingTimeout, stoppingToken);
                await CallBroadcast.EndedAsync(hub, expired, stoppingToken,
                    scope.ServiceProvider.GetRequiredService<IPop.Infrastructure.Persistence.IPopDbContext>(),
                    scope.ServiceProvider.GetService<IPop.Infrastructure.Notifications.Push.PushQueue>());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Call ring timeout sweep failed; will retry next interval.");
            }
        }
    }
}

/// Ends live calls whose media tab stopped sending heartbeats (laptop asleep, tab killed, a phone
/// still online while the laptop that held the call dropped, an API restart that lost the disconnect
/// timer). Clients beat every 20 s; nobody heard from for <see cref="StaleAfter"/> is taken out.
public class CallReaperWorker(IServiceScopeFactory scopes, IHubContext<CallHub> hub, ILogger<CallReaperWorker> log)
    : BackgroundService
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                using var scope = scopes.CreateScope();
                var calls = scope.ServiceProvider.GetRequiredService<ICallService>();
                var reaped = await calls.ReapStaleAsync(DateTime.UtcNow - StaleAfter, stoppingToken);
                if (reaped.Count > 0) log.LogInformation("Ended {Count} call(s) with no live participant tab.", reaped.Count);
                await CallBroadcast.EndedAsync(hub, reaped, stoppingToken,
                    scope.ServiceProvider.GetRequiredService<IPop.Infrastructure.Persistence.IPopDbContext>(),
                    scope.ServiceProvider.GetService<IPop.Infrastructure.Notifications.Push.PushQueue>());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Call reaper sweep failed; will retry next interval.");
            }
        }
    }
}
