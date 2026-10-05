using IPop.Application.Tickets;

namespace IPop.Infrastructure.Tickets;

/// Process-wide copy of iDesk's GET /modules. Queues change rarely, so ten minutes is fresh
/// enough; a stale copy is still served when iDesk is down so the form keeps working.
public sealed class TicketModuleCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private IReadOnlyList<TicketModuleDto>? _modules;
    private DateTime _fetchedAt;

    public IReadOnlyList<TicketModuleDto>? Get(DateTime now, out bool fresh)
    {
        lock (_gate)
        {
            fresh = _modules is not null && now - _fetchedAt < Ttl;
            return _modules;
        }
    }

    public void Set(IReadOnlyList<TicketModuleDto> modules, DateTime now)
    {
        lock (_gate) { _modules = modules; _fetchedAt = now; }
    }
}
