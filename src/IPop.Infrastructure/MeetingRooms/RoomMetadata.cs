using System.Text.Json;
using IPop.Application.MeetingRooms;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.MeetingRooms;

/// The LiveKit room metadata of a hall room: <c>{ recording?: {...}, booking?: {...} }</c>. Every writer (recording
/// start/stop, the recording worker, the booking worker, join, extend) builds the WHOLE document from the database
/// here, so one writer never drops the other's key and a lost write heals on the next one.
/// <list type="bullet">
/// <item><c>recording</c>: <c>{ id, by, startedAt }</c> — the running server recording ("Recording · started by X").</item>
/// <item><c>booking</c>: <c>{ meetingId, organizerId, endAt, nextStartAt?, takeoverAt?, leaving? }</c> — the current
/// booking (RoomAccess). <c>takeoverAt</c> = the next booking's StartAt − 10 min: whoever is not allowed in that booking
/// is removed then. From one minute before it, <c>leaving</c> lists those people's identities so exactly they get the
/// "The next booking starts now" warning. Times are UTC ISO-8601.</item>
/// </list>
public static class RoomMetadata
{
    public static readonly TimeSpan TakeoverWarning = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <paramref name="identities"/> = who is in the LiveKit room now (without EG_ recorders); <paramref name="occupancy"/>
    /// as for RoomAccess. <paramref name="endedRecordingId"/> is left out even if its row still says Recording (the caller
    /// is finishing it).
    public static async Task<string> ComposeAsync(IPopDbContext db, string roomKey, DateTime nowUtc, IReadOnlyCollection<string> identities,
        RoomOccupancy occupancy, CancellationToken ct, Guid? endedRecordingId = null)
    {
        var doc = new Dictionary<string, object>();
        var (current, next) = await RoomAccess.BookingsAsync(db, roomKey, nowUtc, occupancy, ct);
        var rec = await db.MeetingRecordings.AsNoTracking()
            .Where(r => r.RoomKey == roomKey && current != null && r.MeetingId == current.MeetingId && r.Status == MeetingRecordingStatus.Recording && r.EgressId != "" && r.Id != endedRecordingId)
            .Select(r => new { r.Id, r.StartedAt, By = db.Users.Where(u => u.Id == r.StartedById).Select(u => u.Name).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (rec is not null) doc["recording"] = new { id = rec.Id, by = rec.By ?? "", startedAt = Utc(rec.StartedAt) };

        if (current is not null)
        {
            List<string>? leaving = null;
            if (next is not null && nowUtc >= RoomAccess.WindowStart(next) - TakeoverWarning)
            {
                leaving = [];
                foreach (var identity in identities)
                    if (!Guid.TryParse(identity, out var uid) || !RoomAccess.MayEnter(await RoomAccess.RoleAsync(db, next, uid, ct)))
                        leaving.Add(identity);
            }
            doc["booking"] = new
            {
                meetingId = current.MeetingId,
                organizerId = current.OrganizerId,
                endAt = Utc(current.EndAt),
                nextStartAt = next is null ? (DateTime?)null : Utc(next.StartAt),
                takeoverAt = next is null ? (DateTime?)null : Utc(RoomAccess.WindowStart(next)),
                leaving,
            };
        }
        return JsonSerializer.Serialize(doc, Json);
    }

    /// Asks LiveKit who is inside, composes the metadata and writes it unless it equals what was last written to that
    /// room (<paramref name="tracker"/>). Best effort: LiveKit down or the room gone is ignored (the booking worker writes
    /// again within 30 s). Returns the composed JSON, or null when LiveKit could not be asked or written.
    public static async Task<string?> PublishAsync(IPopDbContext db, ILiveKitRoomAdmin liveKit, RoomOccupancyTracker tracker, string roomKey,
        DateTime nowUtc, CancellationToken ct, Guid? endedRecordingId = null, MeetingRoomMedia? mediaRooms = null)
    {
        var media = mediaRooms ?? MeetingRoomMedia.Default;
        var current = (await RoomAccess.BookingsAsync(db, roomKey, nowUtc, tracker.Snapshot(roomKey, nowUtc), ct)).Current;
        if (current is null) return null;
        if (!liveKit.IsConfigured) return null;
        IReadOnlyList<string> identities;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            identities = (await liveKit.ListAsync(media.Room(roomKey, current.MeetingId), ct)).Select(p => p.Identity)
                .Where(i => !i.StartsWith("EG_", StringComparison.Ordinal)).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { tracker.ObserveFailed(roomKey); return null; }
        var seenAt = nowUtc + clock.Elapsed;   // when the listing returned
        var occupancy = tracker.Observe(roomKey, identities.Count > 0, seenAt);
        var json = await ComposeAsync(db, roomKey, seenAt, identities, occupancy, ct, endedRecordingId);
        return await WriteAsync(liveKit, tracker, media.Room(roomKey, current.MeetingId), json, seenAt, ct) ? json : null;
    }

    /// Writes <paramref name="json"/> unless the room already got it recently (<see cref="RoomOccupancyTracker.MetadataDue"/>).
    /// False when the write failed.
    public static async Task<bool> WriteAsync(ILiveKitRoomAdmin liveKit, RoomOccupancyTracker tracker, string roomKey, string json,
        DateTime nowUtc, CancellationToken ct)
    {
        if (!tracker.MetadataDue(roomKey, json, nowUtc)) return true;
        try
        {
            await liveKit.UpdateRoomMetadataAsync(roomKey, json, ct);
            tracker.MetadataWritten(roomKey, json, nowUtc);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            tracker.ForgetMetadata(roomKey);
            return false;
        }
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
