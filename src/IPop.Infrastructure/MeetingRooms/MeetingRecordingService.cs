using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.MeetingRooms;

public sealed class MeetingRecordingService(IPopDbContext db, ILiveKitRoomAdmin liveKit, ILiveKitEgress egress, ISettings settings, IConfiguration config)
    : IMeetingRecordingService
{
    private string OutDir => (config["MeetingRooms:EgressOutDir"] ?? "/out").TrimEnd('/');

    public async Task<MeetingRecordingResult> StartAsync(Guid userId, string key, CancellationToken ct)
    {
        var room = await db.HallRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);
        if (room is null) return new(null, "unknown_room");
        if (!egress.IsConfigured || !liveKit.IsConfigured) return new(null, "egress_unavailable");
        var lkRoom = MeetingRoomKeys.LiveKitRoom(key);
        IReadOnlyList<LiveKitParticipant> inside;
        try { inside = await liveKit.ListAsync(lkRoom, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new(null, "egress_unavailable"); }
        if (inside.All(p => p.Identity != userId.ToString())) return new(null, "not_in_room");
        if (await db.MeetingRecordings.AnyAsync(r => r.RoomKey == key && r.Status == MeetingRecordingStatus.Recording, ct))
            return new(null, "already_recording");

        var now = DateTime.UtcNow;
        var booking = await HallBookings.CurrentAsync(db, key, userId, now, ct);
        var id = Guid.NewGuid();
        var rel = $"{key}/{now.ToLocalTime():yyyy-MM-dd}/{now.ToLocalTime():HHmm}-{id:N}.mp4";
        var row = new MeetingRecording
        {
            Id = id, RoomKey = key, Title = booking?.Title ?? $"{room.Name} · ad-hoc", EgressId = "", StartedById = userId,
            StartedAt = now, Status = MeetingRecordingStatus.Recording, SpoolPath = rel,
        };
        foreach (var p in inside)
            if (Guid.TryParse(p.Identity, out var uid) && row.Viewers.All(v => v.UserId != uid))
                row.Viewers.Add(new MeetingRecordingViewer { RecordingId = id, UserId = uid });
        db.MeetingRecordings.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return new(null, "already_recording"); }   // unique filtered index lost the race

        // From here the row must end consistent even if the caller goes away (closed tab, aborted request):
        // the compensating remove and the EgressId save never use the request token, or a stuck
        // "Recording" row would block the room until the worker gives up on it.
        try { row.EgressId = await egress.StartRoomCompositeAsync(lkRoom, $"{OutDir}/{rel}", ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            db.MeetingRecordings.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
            return new(null, "egress_unavailable");
        }
        await db.SaveChangesAsync(CancellationToken.None);
        await PublishStateAsync(lkRoom, row, CancellationToken.None);
        return new(await DtoAsync(row.Id, ct), null);
    }

    public async Task<MeetingRecordingResult> StopAsync(Guid userId, Guid recordingId, CancellationToken ct)
    {
        var row = await db.MeetingRecordings.FirstOrDefaultAsync(r => r.Id == recordingId && r.Status == MeetingRecordingStatus.Recording, ct);
        if (row is null) return new(null, "not_found");
        IReadOnlyList<LiveKitParticipant> inside;
        try { inside = await liveKit.ListAsync(MeetingRoomKeys.LiveKitRoom(row.RoomKey), ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new(null, "egress_unavailable"); }
        if (inside.All(p => p.Identity != userId.ToString())) return new(null, "not_in_room");
        try { await egress.StopAsync(row.EgressId, ct); }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        { /* already ended; the worker reads the final state */ }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new(null, "egress_unavailable"); }
        row.Status = MeetingRecordingStatus.Processing;
        row.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await PublishStateAsync(MeetingRoomKeys.LiveKitRoom(row.RoomKey), null, ct);
        return new(await DtoAsync(row.Id, ct), null);
    }

    /// Room metadata carries the running server recording so every client shows "Recording · started by X".
    private async Task PublishStateAsync(string lkRoom, MeetingRecording? row, CancellationToken ct)
    {
        var json = row is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(new
        {
            recording = new { id = row.Id, by = await db.Users.Where(u => u.Id == row.StartedById).Select(u => u.Name).FirstAsync(ct), startedAt = row.StartedAt },
        });
        try { await liveKit.UpdateRoomMetadataAsync(lkRoom, json, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
    }

    public async Task NoteJoinAsync(Guid userId, string key, CancellationToken ct)
    {
        var rec = await db.MeetingRecordings.Where(r => r.RoomKey == key && r.Status == MeetingRecordingStatus.Recording)
            .Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (rec == Guid.Empty || await db.MeetingRecordingViewers.AnyAsync(v => v.RecordingId == rec && v.UserId == userId, ct)) return;
        var viewer = new MeetingRecordingViewer { RecordingId = rec, UserId = userId };
        db.MeetingRecordingViewers.Add(viewer);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // A concurrent join may have inserted this viewer after our existence check.
            db.Entry(viewer).State = EntityState.Detached;
        }
    }

    public async Task<bool> CanViewAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct) =>
        await db.MeetingRecordings.AnyAsync(r => r.Id == recordingId && r.Status != MeetingRecordingStatus.Deleted
            && (admin || r.Viewers.Any(v => v.UserId == userId)), ct);

    public async Task<IReadOnlyList<MeetingRecordingDto>> ListAsync(Guid userId, bool admin, CancellationToken ct)
    {
        var ids = await db.MeetingRecordings.AsNoTracking()
            .Where(r => r.Status != MeetingRecordingStatus.Deleted && r.Status != MeetingRecordingStatus.Recording
                && (admin || r.Viewers.Any(v => v.UserId == userId)))
            .OrderByDescending(r => r.StartedAt).Select(r => r.Id).Take(200).ToListAsync(ct);
        var list = new List<MeetingRecordingDto>();
        foreach (var id in ids) list.Add(await DtoAsync(id, ct));
        return list;
    }

    public async Task<bool> DeleteAsync(Guid recordingId, CancellationToken ct)
    {
        var row = await db.MeetingRecordings.FirstOrDefaultAsync(r => r.Id == recordingId && r.Status != MeetingRecordingStatus.Deleted, ct);
        if (row is null || row.Status == MeetingRecordingStatus.Recording) return false;
        row.Status = MeetingRecordingStatus.Deleted;   // the worker removes the NAS file
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<MeetingRecordingDto> DtoAsync(Guid id, CancellationToken ct)
    {
        var days = await settings.GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct);
        var r = await db.MeetingRecordings.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Id, x.RoomKey, x.Title, x.StartedAt, x.DurationSeconds, x.SizeBytes, x.Status, People = x.Viewers.Count,
            By = db.Users.Where(u => u.Id == x.StartedById).Select(u => u.Name).FirstOrDefault(),
        }).SingleAsync(ct);
        var name = HallBooking.Rooms.FirstOrDefault(x => x.Key == r.RoomKey).Name ?? r.RoomKey;
        return new(r.Id, r.RoomKey, name, r.Title, r.By ?? "", r.StartedAt, r.DurationSeconds, r.SizeBytes, r.People,
            r.Status.ToString().ToLowerInvariant(), r.StartedAt.AddDays(days));
    }
}
