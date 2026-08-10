using IPop.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Presence;

public sealed class DirectoryChangeNotifier(IHubContext<PresenceHub> hub) : IDirectoryChangeNotifier
{
    public Task NotifyAsync(CancellationToken ct = default)
        => hub.Clients.All.SendAsync("directoryChanged", ct);
}
