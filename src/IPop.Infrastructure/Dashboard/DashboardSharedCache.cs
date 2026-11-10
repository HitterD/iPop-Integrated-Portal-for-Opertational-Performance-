namespace IPop.Infrastructure.Dashboard;

public sealed record DashboardSystemSnapshot(int OutboxFailing, DateTime? HrisStartedAt, bool HrisSucceeded);

/// Dashboard parts that are the same for every user, kept for a few seconds so a login burst does not
/// re-count the mail outbox and re-read the last HRIS run once per person. Singleton.
public sealed class DashboardSharedCache
{
    public static readonly TimeSpan SystemTtl = TimeSpan.FromSeconds(15);
    private SystemEntry? _system;

    public bool TryGetSystem(DateTime now, out DashboardSystemSnapshot value)
    {
        var entry = _system;
        if (entry is not null && now - entry.At < SystemTtl) { value = entry.Value; return true; }
        value = default!;
        return false;
    }

    public void SetSystem(DashboardSystemSnapshot value, DateTime now) => _system = new SystemEntry(value, now);

    private sealed record SystemEntry(DashboardSystemSnapshot Value, DateTime At);
}
