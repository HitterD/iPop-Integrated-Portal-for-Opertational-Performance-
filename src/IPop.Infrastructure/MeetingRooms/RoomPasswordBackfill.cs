using IPop.Application.Hall;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.MeetingRooms;

/// Runs after schema migration and before requests/workers. Does not rewrite old notifications or log codes.
public static class RoomPasswordBackfill
{
    public static async Task<int> RunAsync(IPopDbContext db, DateTime nowUtc, CancellationToken ct, ILogger? log = null)
    {
        var rows = await db.Meetings.AsNoTracking().Where(m => m.Status != MeetingStatus.Cancelled && m.EndAt > nowUtc)
            .Select(m => new { m.Id, m.Location, m.RoomPassword }).ToListAsync(ct);
        var count = 0;
        foreach (var row in rows.Where(m => HallBooking.RoomKey(m.Location) is not null && string.IsNullOrWhiteSpace(m.RoomPassword)))
        {
            // Conditional update makes concurrent startup passes idempotent without overwriting a regenerated code.
            count += await db.Meetings.Where(m => m.Id == row.Id && m.RoomPassword == row.RoomPassword
                && m.Status != MeetingStatus.Cancelled && m.EndAt > nowUtc)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.RoomPassword, RoomPasswords.New()), ct);
        }
        log?.LogInformation("Backfilled passwords for {Count} eligible hall bookings", count);
        return count;
    }
}
