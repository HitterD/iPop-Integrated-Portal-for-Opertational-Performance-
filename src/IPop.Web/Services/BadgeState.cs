using IPop.Application.Realtime;
using IPop.Web.Models;

namespace IPop.Web.Services;

/// Unread counts for the sidebar, seeded from GET /api/me/badges and kept live
/// by the hub's badgeChanged event.
public class BadgeState(AuthState auth)
{
    private readonly Dictionary<string, int> _counts = [];
    private IDisposable? _sub;
    private bool _reconnectHooked;

    public event Action? Changed;

    public int this[string key] => _counts.TryGetValue(key, out var n) ? n : 0;
    public bool Any(string key) => this[key] > 0;

    public async Task StartAsync()
    {
        _sub ??= auth.On<string, int>(RealtimeEvents.BadgeChanged, (key, count) => Set(key, count));
        // badgeChanged pushes sent while the hub was down are lost — re-seed once it is back.
        if (!_reconnectHooked) { auth.HubReconnected += OnHubReconnected; _reconnectHooked = true; }
        await RefreshAsync();
    }

    /// Re-reads every count from the server. Called after an action that changes one
    /// (marking notifications read, for instance) so the sidebar agrees immediately.
    public async Task RefreshAsync()
    {
        var seed = await auth.GetAsync<Dictionary<string, int>>("/api/me/badges");
        if (seed is null) return;
        _counts.Clear();
        foreach (var (k, v) in seed) _counts[k] = v;
        Changed?.Invoke();
    }

    public void Set(string key, int count)
    {
        if (_counts.TryGetValue(key, out var old) && old == count) return;
        _counts[key] = count;
        Changed?.Invoke();
    }

    private void OnHubReconnected() => _ = RefreshSafeAsync();

    private async Task RefreshSafeAsync()
    {
        try { await RefreshAsync(); } catch { /* next push or navigation re-seeds */ }
    }

    public void Stop()
    {
        _sub?.Dispose(); _sub = null;
        if (_reconnectHooked) { auth.HubReconnected -= OnHubReconnected; _reconnectHooked = false; }
    }
    public void Clear() { _counts.Clear(); Changed?.Invoke(); }
}
