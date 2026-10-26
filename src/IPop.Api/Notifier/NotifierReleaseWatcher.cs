using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Notifier;

/// Publishing a Notifier release rewrites release.json; within half a minute every connected app hears
/// "notifierUpdate" on /hubs/notification and checks its feed (it also checks every 6 hours on its own).
public sealed class NotifierReleaseWatcher(NotifierReleases releases, IHubContext<IPop.Api.Notifications.NotificationHub> hub,
    ILogger<NotifierReleaseWatcher> log) : BackgroundService
{
    public const string Event = "notifierUpdate";
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var last = releases.Stamp();
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = releases.Stamp();
            if (now is null || now == last) continue;
            last = now;
            var state = releases.Read();
            log.LogInformation("Notifier release changed: all {All}, pilot {Pilot}", state.All?.Version, state.Pilot?.Version);
            try { await hub.Clients.All.SendAsync(Event, new { all = state.All?.Version, pilot = state.Pilot?.Version }, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "notifierUpdate broadcast failed"); }
        }
    }
}
