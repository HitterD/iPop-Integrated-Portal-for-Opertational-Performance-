using IPop.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Presence;

public sealed class AuditChangeNotifier(IHubContext<PresenceHub> hub) : IAuditChangeNotifier
{
    public Task NotifyAsync(CancellationToken ct = default)
        => hub.Clients.All.SendAsync("auditChanged", ct);
}
