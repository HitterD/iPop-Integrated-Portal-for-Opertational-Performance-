using IPop.Application.Files;
using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.MeetingRooms;

public sealed class MeetingRecordingService(IPopDbContext db, ILiveKitRoomAdmin liveKit, ILiveKitEgress egress, ISettings settings, IConfiguration config,
    RoomOccupancyTracker? occupancyTracker = null, IFileAccessService? nas = null, RecordingFileCache? fileCache = null,
    ILogger<MeetingRecordingService>? log = null)
    : IMeetingRecordingService
{
    private readonly RoomOccupancyTracker occupancy = occupancyTracker ?? new RoomOccupancyTracker();
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
        // Same booking as join and the room list (early window, overrun while occupied).
        var occupied = occupancy.Observe(key, inside.Any(p => !p.Identity.StartsWith("EG_", StringComparison.Ordinal)), now);
        var booking = (await RoomAccess.ForAsync(db, key, userId, now, occupied, ct)).Current;
        var id = Guid.NewGuid();
        var rel = $"{key}/{now.ToLocalTime():yyyy-MM-dd}/{now.ToLocalTime():HHmm}-{id:N}.mp4";
        var row = new MeetingRecording
        {
            Id = id, RoomKey = key, Title = booking?.Title ?? $"{room.Name} · ad-hoc", EgressId = "", StartedById = userId, MeetingId = booking?.MeetingId,
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
        await PublishStateAsync(row.RoomKey, null, CancellationToken.None);
        var starter = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(CancellationToken.None);
        await MeetingChatService.PostSystemAsync(db, liveKit, row, $"{(string.IsNullOrWhiteSpace(starter) ? "Someone" : starter)} started recording",
            DateTime.UtcNow, log, CancellationToken.None);
        return new(await DtoAsync(row.Id, userId, false, ct), null);
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
        await PublishStateAsync(row.RoomKey, row.Id, ct);
        await MeetingChatService.PostSystemAsync(db, liveKit, row, "Recording stopped", row.EndedAt.Value, log, CancellationToken.None);
        return new(await DtoAsync(row.Id, userId, false, ct), null);
    }

    /// Room metadata carries the running server recording so every client shows "Recording · started by X"; it is
    /// composed with the booking info (<see cref="RoomMetadata"/>), never replacing it.
    private Task PublishStateAsync(string roomKey, Guid? endedRecordingId, CancellationToken ct) =>
        RoomMetadata.PublishAsync(db, liveKit, occupancy, roomKey, DateTime.UtcNow, ct, endedRecordingId);

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

    private IQueryable<MeetingRecording> Listed => db.MeetingRecordings.AsNoTracking()
        .Where(r => r.Status != MeetingRecordingStatus.Deleted && r.Status != MeetingRecordingStatus.Recording);

    /// Only recordings the user was present for. Admins are not merged in: they audit from Administration.
    public async Task<IReadOnlyList<MeetingRecordingDto>> ListAsync(Guid userId, bool admin, CancellationToken ct)
    {
        var ids = await Listed.Where(r => r.Viewers.Any(v => v.UserId == userId))
            .OrderByDescending(r => r.StartedAt).Select(r => r.Id).Take(200).ToListAsync(ct);
        var list = new List<MeetingRecordingDto>();
        foreach (var id in ids) list.Add(await DtoAsync(id, userId, admin, ct));
        return list;
    }

    public async Task<IReadOnlyList<MeetingRecordingDto>> ListAllAsync(CancellationToken ct)
    {
        var ids = await Listed.OrderByDescending(r => r.StartedAt).Select(r => r.Id).Take(200).ToListAsync(ct);
        var list = new List<MeetingRecordingDto>();
        foreach (var id in ids) list.Add(await DtoAsync(id, Guid.Empty, true, ct));
        return list;
    }

    public Task<bool> CanDeleteAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct) =>
        db.MeetingRecordings.AnyAsync(r => r.Id == recordingId && r.Status != MeetingRecordingStatus.Deleted
            && (admin || r.StartedById == userId
                || (r.MeetingId != null && db.Meetings.Any(m => m.Id == r.MeetingId && m.OrganizerId == userId))), ct);

    public async Task<string?> DeleteAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct)
    {
        var row = await db.MeetingRecordings.FirstOrDefaultAsync(r => r.Id == recordingId && r.Status != MeetingRecordingStatus.Deleted, ct);
        if (row is null) return "not_found";
        if (!await CanDeleteAsync(userId, admin, recordingId, ct))
            // Someone who could not even see it learns nothing about its existence.
            return await CanViewAsync(userId, admin, recordingId, ct) ? "forbidden" : "not_found";
        if (row.Status == MeetingRecordingStatus.Recording) return "recording";

        row.Status = MeetingRecordingStatus.Deleted;
        await db.SaveChangesAsync(ct);
        // The chat saved with it goes with it (its booking's chat was kept only for this recording).
        await DeleteChatAsync(db, row.Id, CancellationToken.None);
        // The row is Deleted from here on (players and tickets answer 404); the file cleanup must finish even if the
        // caller goes away, so it does not use the request token.
        if (row.NasPath is { } path && nas is { HasServiceAccount: true })
        {
            try
            {
                await nas.DeleteAsServiceAsync(path, CancellationToken.None);
                row.NasPath = null;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                // NasPath stays: the nightly purge retries Deleted rows that still have a file.
                log?.LogWarning(e, "Meeting recording {RecordingId}: NAS delete failed, the nightly purge will retry", row.Id);
            }
        }
        DeleteSpool(row);
        if (fileCache is not null) await fileCache.EvictAsync(row.Id, CancellationToken.None);
        return null;
    }

    /// Removes the in-meeting chat lines saved with a recording (delete for everyone, and the retention purge).
    public static Task<int> DeleteChatAsync(IPopDbContext db, Guid recordingId, CancellationToken ct) =>
        db.MeetingChatMessages.Where(m => m.RecordingId == recordingId).ExecuteDeleteAsync(ct);

    /// A Processing recording deleted before the upload: its finished file in the spool folder goes too (best effort).
    private void DeleteSpool(MeetingRecording row)
    {
        if (string.IsNullOrEmpty(row.SpoolPath)) return;
        try
        {
            var spool = config["MeetingRooms:SpoolDir"] ?? @"F:\ipop-data\recordings-spool";
            var local = Path.Combine(spool, row.SpoolPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(local)) File.Delete(local);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private async Task<MeetingRecordingDto> DtoAsync(Guid id, Guid userId, bool admin, CancellationToken ct)
    {
        var days = await settings.GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct);
        var r = await db.MeetingRecordings.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Id, x.RoomKey, x.Title, x.StartedAt, x.DurationSeconds, x.SizeBytes, x.Status, People = x.Viewers.Count,
            By = db.Users.Where(u => u.Id == x.StartedById).Select(u => u.Name).FirstOrDefault(),
            Chat = db.MeetingChatMessages.Count(m => m.RecordingId == x.Id),
            CanDelete = admin || x.StartedById == userId
                || (x.MeetingId != null && db.Meetings.Any(m => m.Id == x.MeetingId && m.OrganizerId == userId)),
        }).SingleAsync(ct);
        var name = HallBooking.Rooms.FirstOrDefault(x => x.Key == r.RoomKey).Name ?? r.RoomKey;
        return new(r.Id, r.RoomKey, name, r.Title, r.By ?? "", r.StartedAt, r.DurationSeconds, r.SizeBytes, r.People,
            r.Status.ToString().ToLowerInvariant(), r.StartedAt.AddDays(days), r.CanDelete, ChatCount: r.Chat);
    }
}
