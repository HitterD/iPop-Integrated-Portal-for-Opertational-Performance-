namespace IPop.Application.Realtime;

/// Server → client push. Endpoints and workers depend on this, never on SignalR hub types,
/// so a backplane (Redis) later is a registration change.
public interface IRealtime
{
    Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default);
    Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default);
    Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default);
    Task ToGroupAsync(string group, string evt, object? payload = null, CancellationToken ct = default);
}

/// A module's unread count for the signed-in user, surfaced by GET /api/me/badges.
public interface IBadgeProvider
{
    /// Nav badge key, e.g. "chat", "mail", "notif".
    string Key { get; }
    Task<int> CountAsync(Guid userId, CancellationToken ct);
}

/// Wrap multi-argument events (e.g. presenceChanged(nik, status)) so they reach the client
/// as separate arguments rather than one object.
public sealed record RealtimeArgs(params object?[] Values);
