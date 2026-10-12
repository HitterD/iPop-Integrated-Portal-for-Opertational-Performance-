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

    public static async Task<bool> EndPreviousAsync(IPopDbContext db, ILiveKitEgress? egress, MeetingRoomMedia media,
        string key, Guid? current, DateTime now, CancellationToken ct)
    {
        var rows = await db.MeetingRecordings.Where(r => r.RoomKey == key && r.MeetingId != current
            && (r.Status == MeetingRecordingStatus.Recording || r.Status == MeetingRecordingStatus.Processing)).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (egress is not { IsConfigured: true } || string.IsNullOrEmpty(row.EgressId)) return false;
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
