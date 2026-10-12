using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.MeetingRooms;

/// An accepted StopEgress is not proof: only a terminal response releases the physical room's recording slot.
public static class RecordingBoundary
{
    public static bool Terminal(string status) => status is "EGRESS_COMPLETE" or "EGRESS_LIMIT_REACHED" or "EGRESS_FAILED" or "EGRESS_ABORTED";
    public static bool Belongs(MeetingRoomMedia media, MeetingRecording row, LiveKitEgressInfo info) =>
        row.MeetingId is { } id && info.Room == media.Room(row.RoomKey, id);

    /// A lost Egress id may mean the remote start succeeded before our process died. List the booking room's
    /// Egress jobs, stop each nonterminal job, and re-query: only an authoritative terminal/empty list releases it.
    public static async Task<bool> ReconcileInterruptedAsync(IPopDbContext db, ILiveKitEgress? egress, MeetingRoomMedia media,
        MeetingRecording row, DateTime now, CancellationToken ct)
    {
        if (row.Status != MeetingRecordingStatus.Recording || row.StartedAt >= now - MeetingRecordingWorker.StartGrace
            || row.MeetingId is not { } id || egress is not { IsConfigured: true }) return false;
        try
        {
            var room = media.Room(row.RoomKey, id);
            var jobs = await egress.ListForRoomAsync(room, ct);
            if (jobs.Any(j => !Belongs(media, row, j.Info) || string.IsNullOrWhiteSpace(j.Id))) return false;
            foreach (var job in jobs.Where(j => !Terminal(j.Info.Status)))
                await egress.StopAsync(job.Id, ct);
            var confirmed = await egress.ListForRoomAsync(room, ct);
            if (confirmed.Any(j => !Belongs(media, row, j.Info) || !Terminal(j.Info.Status))) return false;
            row.Status = MeetingRecordingStatus.Failed;
            row.Error = "start interrupted; room egress cessation confirmed";
            row.EndedAt ??= now;
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    public static async Task<bool> EndPreviousAsync(IPopDbContext db, ILiveKitEgress? egress, MeetingRoomMedia media,
        string key, Guid? current, DateTime now, CancellationToken ct)
    {
        var rows = await db.MeetingRecordings.Where(r => r.RoomKey == key && r.MeetingId != current
            && (r.Status == MeetingRecordingStatus.Recording || r.Status == MeetingRecordingStatus.Processing)).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.EgressId))
            {
                if (!await ReconcileInterruptedAsync(db, egress, media, row, now, ct)) return false;
                continue;
            }
            if (egress is not { IsConfigured: true }) return false;
            try
            {
                var info = await egress.GetAsync(row.EgressId, ct);
                if (info is null || !Belongs(media, row, info))
                {
                    // A copied recording of a different namespace is quarantined: never stop its Egress.
                    if (info?.Room is { } room && media.Parse(room) is null) continue;
                    return false;
                }
                if (!Terminal(info.Status))
                {
                    await egress.StopAsync(row.EgressId, ct);
                    info = await egress.GetAsync(row.EgressId, ct);
                }
                if (info is null || !Belongs(media, row, info) || !Terminal(info.Status)) return false;
                if (row.Status == MeetingRecordingStatus.Recording)
                {
                    row.Status = MeetingRecordingStatus.Processing;
                    row.EndedAt ??= now;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return false; }
        }
        return true;
    }
}
