using Microsoft.AspNetCore.SignalR.Client;

namespace IPop.Web.Services;

/// WithAutomaticReconnect gives up after its schedule and raises Closed; without this the hub
/// stayed dead until a page refresh. Retries StartAsync on a widening backoff while the owner
/// still wants the connection (signed in, not replaced/disposed).
public static class HubRetry
{
    /// 5s, 15s, 30s, then every 60s.
    public static TimeSpan Delay(int attempt) => attempt switch
    {
        0 => TimeSpan.FromSeconds(5),
        1 => TimeSpan.FromSeconds(15),
        2 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(60),
    };

    public static async Task RunAsync(HubConnection conn, Func<bool> stillWanted, Action onStarted)
    {
        for (var attempt = 0; ; attempt++)
        {
            await Task.Delay(Delay(attempt));
            if (!stillWanted()) return;
            if (conn.State != HubConnectionState.Disconnected) return; // someone else restarted it
            try { await conn.StartAsync(); }
            catch { continue; }
            if (stillWanted()) onStarted();
            return;
        }
    }
}
