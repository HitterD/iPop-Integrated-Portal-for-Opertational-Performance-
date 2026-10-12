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

    /// <paramref name="identities"/> = who is in the LiveKit room now (without EG_ recorders); non-empty means occupied.
    /// <paramref name="endedRecordingId"/> is left out even if its row still says Recording (the caller is finishing it).
    public static async Task<string> ComposeAsync(IPopDbContext db, string roomKey, DateTime nowUtc, IReadOnlyCollection<string> identities,
        CancellationToken ct, Guid? endedRecordingId = null)
    {
        var doc = new Dictionary<string, object>();
        var rec = await db.MeetingRecordings.AsNoTracking()
            .Where(r => r.RoomKey == roomKey && r.Status == MeetingRecordingStatus.Recording && r.EgressId != "" && r.Id != endedRecordingId)
            .Select(r => new { r.Id, r.StartedAt, By = db.Users.Where(u => u.Id == r.StartedById).Select(u => u.Name).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (rec is not null) doc["recording"] = new { id = rec.Id, by = rec.By ?? "", startedAt = Utc(rec.StartedAt) };

        var (current, next) = await RoomAccess.BookingsAsync(db, roomKey, nowUtc, identities.Count > 0, ct);
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

    /// Asks LiveKit who is inside, composes and writes the metadata. Best effort: LiveKit down or the room gone is
    /// ignored (the booking worker writes it again within 30 s). Returns what was written, or null.
    public static async Task<string?> PublishAsync(IPopDbContext db, ILiveKitRoomAdmin liveKit, string roomKey, DateTime nowUtc,
        CancellationToken ct, Guid? endedRecordingId = null)
    {
        if (!liveKit.IsConfigured) return null;
        try
        {
            var lkRoom = MeetingRoomKeys.LiveKitRoom(roomKey);
            var identities = (await liveKit.ListAsync(lkRoom, ct)).Select(p => p.Identity)
                .Where(i => !i.StartsWith("EG_", StringComparison.Ordinal)).ToList();
            var json = await ComposeAsync(db, roomKey, nowUtc, identities, ct, endedRecordingId);
            await liveKit.UpdateRoomMetadataAsync(lkRoom, json, ct);
            return json;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
