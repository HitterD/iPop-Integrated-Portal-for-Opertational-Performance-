using IPop.Application.Realtime;

namespace IPop.Api.Realtime;

public sealed class AuditChangeNotifier(IRealtime realtime) : IAuditChangeNotifier
{
    public Task NotifyAsync(CancellationToken ct = default)
        => realtime.ToAllAsync(RealtimeEvents.AuditChanged, ct: ct);
}
