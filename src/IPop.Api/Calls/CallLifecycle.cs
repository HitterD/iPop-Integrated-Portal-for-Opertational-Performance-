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

/// Counts open /hubs/calls connections per user. When the last one closes (tab closed,
/// laptop asleep) and does not come back within the grace period, the user's calls end —
/// otherwise the other side would stare at a frozen call and "ringing" rows would never clear.
public class CallConnections(IServiceScopeFactory scopes, IHubContext<CallHub> hub, ILogger<CallConnections> log)
{
    /// Covers page refreshes and SignalR's automatic reconnect.
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private readonly Dictionary<Guid, int> open = [];

    public void Connected(Guid userId)
    {
        lock (gate) open[userId] = open.GetValueOrDefault(userId) + 1;
    }

    public void Disconnected(Guid userId)
    {
        lock (gate)
        {
            var left = open.GetValueOrDefault(userId) - 1;
            if (left > 0) { open[userId] = left; return; }
            open.Remove(userId);
        }
        _ = EndAfterGraceAsync(userId);
    }

    public bool IsConnected(Guid userId)
    {
        lock (gate) return open.ContainsKey(userId);
    }

    private async Task EndAfterGraceAsync(Guid userId)
    {
        try
        {
            await Task.Delay(Grace);
            if (IsConnected(userId)) return;

            using var scope = scopes.CreateScope();
            var calls = scope.ServiceProvider.GetRequiredService<ICallService>();
            var ended = await calls.EndForUserAsync(userId, CancellationToken.None);
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

/// Unanswered rings become "missed" after <see cref="CallService.RingTimeout"/>.
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
