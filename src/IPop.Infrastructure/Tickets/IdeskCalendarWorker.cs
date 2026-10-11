using IPop.Application.Tickets;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Tickets;

/// Keeps iDesk Zoom bookings on iPop calendars when webhooks were missed: a full pass shortly after
/// start (also the first backfill after deploy), then every night around 02:00 WIB. Each pass replaces
/// every active user's iDesk entries from 7 days ago to 92 days ahead with what iDesk lists.
/// Also forgets processed webhook ids older than 30 days.
public sealed class IdeskCalendarWorker(IServiceScopeFactory scopes, ILogger<IdeskCalendarWorker> log) : BackgroundService
{
    private static readonly TimeSpan Wib = TimeSpan.FromHours(7);
    public const int PastDays = 7;
    public const int AheadDays = 85;   // iDesk allows 92 days per request: -7 .. +85

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "IdeskCalendarWorker pass failed"); }
            try { await Task.Delay(UntilNextRun(DateTime.UtcNow), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// Time until the next 02:00 WIB.
    public static TimeSpan UntilNextRun(DateTime utcNow)
    {
        var local = utcNow + Wib;
        var next = local.Date.AddHours(2);
        if (next <= local) next = next.AddDays(1);
        return next - local;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var idesk = scope.ServiceProvider.GetRequiredService<IIdeskClient>();
        if (!idesk.IsConfigured) return 0;

        var cutoff = DateTime.UtcNow.AddDays(-30);
        await db.IdeskWebhookReceipts.Where(r => r.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow + Wib);
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.Nik)
            .Select(u => new { u.Id, u.Nik }).ToListAsync(ct);
        int ok = 0, failed = 0;
        foreach (var u in users)
        {
            // A fresh scope per user keeps the change tracker small over a few thousand users.
            using var userScope = scopes.CreateScope();
            var sync = userScope.ServiceProvider.GetRequiredService<IdeskCalendarSync>();
            if (await sync.ResyncUserAsync(u.Id, u.Nik, today.AddDays(-PastDays), today.AddDays(AheadDays), ct)) ok++;
            else if (++failed >= 20 && ok == 0)
            {
                log.LogWarning("iDesk calendar resync stopped: iDesk did not answer for the first {Count} users", failed);
                break;
            }
        }
        log.LogInformation("iDesk calendar resync: {Ok} users synced, {Failed} failed", ok, failed);
        return ok;
    }
}
