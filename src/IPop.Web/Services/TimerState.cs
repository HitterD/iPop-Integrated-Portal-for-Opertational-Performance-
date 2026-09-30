using System.Net.Http.Json;
using IPop.Application.Projects;
using IPop.Application.Realtime;

namespace IPop.Web.Services;

/// The signed-in user's running task timer (Phase 3), seeded from GET /api/projects/timer and
/// kept live by the hub's timerChanged event so every tab and the topbar agree.
public class TimerState(AuthState auth)
{
    private IDisposable? _sub;
    private bool _reconnectHooked;

    public RunningTimerDto? Running { get; private set; }
    public event Action? Changed;

    public bool IsRunning(Guid taskId) => Running?.TaskId == taskId;

    public async Task StartAsync()
    {
        _sub ??= auth.On(RealtimeEvents.TimerChanged, () => _ = RefreshSafeAsync());
        if (!_reconnectHooked) { auth.HubReconnected += OnHubReconnected; _reconnectHooked = true; }
        await RefreshSafeAsync();
    }

    public async Task RefreshAsync()
    {
        Running = await auth.GetAsync<RunningTimerDto>("/api/projects/timer");
        Changed?.Invoke();
    }

    /// Starts a timer on the task (stopping any other). Returns an error code, or null on success.
    public async Task<string?> StartTimerAsync(Guid taskId)
    {
        var res = await auth.SendAsync(HttpMethod.Post, $"/api/projects/tasks/{taskId}/timer/start");
        if (!res.IsSuccessStatusCode) return await ErrorCodeAsync(res);
        Running = await res.Content.ReadFromJsonAsync<RunningTimerDto>();
        Changed?.Invoke();
        return null;
    }

    /// Stops the running timer. Returns an error code, or null on success.
    public async Task<string?> StopTimerAsync()
    {
        var res = await auth.SendAsync(HttpMethod.Post, "/api/projects/timer/stop");
        if (!res.IsSuccessStatusCode) return await ErrorCodeAsync(res);
        Running = null;
        Changed?.Invoke();
        return null;
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage res)
    {
        try
        {
            var body = await res.Content.ReadFromJsonAsync<Dictionary<string, object?>>();
            return body?.GetValueOrDefault("code")?.ToString() ?? $"http_{(int)res.StatusCode}";
        }
        catch { return $"http_{(int)res.StatusCode}"; }
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
        Running = null;
        Changed?.Invoke();
    }
}
