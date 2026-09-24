using IPop.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Realtime;

/// The single hub (/hubs/app). Every connection joins its user group so IRealtime.ToUserAsync
/// reaches all of that user's tabs; conversation groups are joined by module hub methods.
public class AppHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var nik = Context.User?.FindFirst("nik")?.Value;
        if (!string.IsNullOrEmpty(nik))
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeEvents.UserGroup(nik));
        await base.OnConnectedAsync();
    }
}

public sealed class HubRealtime(IHubContext<AppHub> hub) : IRealtime
{
    public Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default) =>
        Send(hub.Clients.All, evt, payload, ct);

    public Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default) =>
        Send(hub.Clients.Group(RealtimeEvents.UserGroup(nik)), evt, payload, ct);

    public Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default) =>
        Send(hub.Clients.Groups(niks.Distinct().Select(RealtimeEvents.UserGroup).ToList()), evt, payload, ct);

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
