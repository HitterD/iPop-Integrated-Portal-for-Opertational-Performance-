using IPop.Application.Realtime;

namespace IPop.Api.Realtime;

public sealed class DirectoryChangeNotifier(IRealtime realtime) : IDirectoryChangeNotifier
{
    public Task NotifyAsync(CancellationToken ct = default)
        => realtime.ToAllAsync(RealtimeEvents.DirectoryChanged, ct: ct);
}
