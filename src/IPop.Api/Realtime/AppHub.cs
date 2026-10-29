using IPop.Api.Presence;
using IPop.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Realtime;

/// The single hub (/hubs/app). Every connection joins its user group so IRealtime.ToUserAsync
/// reaches all of that user's tabs; conversation groups are joined by module hub methods.
public class AppHub(PresenceConnections connections, IConfiguration cfg) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var nik = Context.User?.FindFirst("nik")?.Value;
        if (!string.IsNullOrEmpty(nik))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeEvents.UserGroup(nik));
            connections.Connected(nik);
            // "Sign in with browser" in the iPop Notifier reaches the open tabs on the same address (discovery only).
            if (Context.GetHttpContext() is { } http)
                IPop.Api.Notifier.NotifierLinkStore.Shared.TabConnected(Context.ConnectionId, IPop.Api.Notifier.ClientAddress.Of(http, cfg));
        }
        await base.OnConnectedAsync();
    }

    /// The last tab closing starts the offline grace period (see PresenceConnections).
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var nik = Context.User?.FindFirst("nik")?.Value;
        if (!string.IsNullOrEmpty(nik)) connections.Disconnected(nik);
        IPop.Api.Notifier.NotifierLinkStore.Shared.TabDisconnected(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

public sealed class HubRealtime(IHubContext<AppHub> hub, IHubContext<IPop.Api.Notifications.NotificationHub> notif) : IRealtime
{
    /// The iPop Notifier listens on /hubs/notification only; the bell refresh goes there too.
    private Task Mirror(string evt, Func<IHubClients, IClientProxy> target, object? payload, CancellationToken ct) =>
        evt == RealtimeEvents.NotificationsChanged ? Send(target(notif.Clients), evt, payload, ct) : Task.CompletedTask;

    public Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default) =>
        Send(hub.Clients.All, evt, payload, ct);

    public Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default) =>
        Task.WhenAll(Send(hub.Clients.Group(RealtimeEvents.UserGroup(nik)), evt, payload, ct),
            Mirror(evt, c => c.Group(RealtimeEvents.UserGroup(nik)), payload, ct));

    public Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default)
    {
        var groups = niks.Distinct().Select(RealtimeEvents.UserGroup).ToList();
        return Task.WhenAll(Send(hub.Clients.Groups(groups), evt, payload, ct), Mirror(evt, c => c.Groups(groups), payload, ct));
    }

    public Task ToGroupAsync(string group, string evt, object? payload = null, CancellationToken ct = default) =>
        Send(hub.Clients.Group(group), evt, payload, ct);

    /// Payload-less events go out with no arguments (the existing zero-arg client handlers);
    /// RealtimeArgs spreads into positional arguments.
    internal static object?[] Args(object? payload) => payload switch
    {
        null => [],
        RealtimeArgs a => a.Values,
        _ => [payload],
    };

    private static Task Send(IClientProxy target, string evt, object? payload, CancellationToken ct) =>
        target.SendCoreAsync(evt, Args(payload), ct);
}
