using IPop.Application.Calls;
using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.MeetingRooms;

/// The five SJA Hall rooms as drop-in meeting rooms. LiveKit is the truth for who is inside.
public sealed class MeetingRoomService(IPopDbContext db, ILiveKitRoomAdmin liveKit, ILiveKitTokenService tokens, ISettings settings)
    : IMeetingRoomService
{
    public const string PrivateTitle = "Booked";

    public async Task<IReadOnlyList<MeetingRoomDto>> ListAsync(Guid viewerId, CancellationToken ct)
    {
        var rooms = await db.HallRooms.AsNoTracking().OrderBy(r => r.SortOrder).ToListAsync(ct);
        // LiveKit first and in parallel (each call may take its 5 s timeout); one unreachable room only marks that room.
        var inside = await Task.WhenAll(rooms.Select(r => ParticipantsAsync(r.Key, ct)));
        var names = await NamesAsync(inside.Where(x => x is not null).SelectMany(x => x!), ct);
        var video = await VideoAsync(ct);
        var now = DateTime.UtcNow;
        var recordings = await db.MeetingRecordings.AsNoTracking()
            .Where(r => r.Status == MeetingRecordingStatus.Recording)
            .Select(r => new { r.RoomKey, r.Id, r.StartedAt, Name = db.Users.Where(u => u.Id == r.StartedById).Select(u => u.Name).FirstOrDefault() })
            .ToListAsync(ct);
        var list = new List<MeetingRoomDto>();
        for (var i = 0; i < rooms.Count; i++)
        {
            var room = rooms[i];
            var available = inside[i] is not null;
            var people = available ? People(inside[i]!, names) : [];
            var current = await HallBookings.CurrentAsync(db, room.Key, viewerId, now, ct);
            var next = await HallBookings.NextAsync(db, room.Key, viewerId, now, ct);
            var rec = recordings.FirstOrDefault(r => r.RoomKey == room.Key);
            list.Add(new MeetingRoomDto(room.Key, room.Name, room.Capacity, video[room.Key], available, people,
                current is null ? null : new MeetingRoomBookingDto(current.MeetingId, current.Allowed ? current.Title : PrivateTitle, now, current.EndAt, null),
                next is null ? null : new MeetingRoomBookingDto(next.MeetingId, next.Allowed ? next.Title : PrivateTitle, next.StartAt, next.EndAt,
                    next.Allowed ? next.OrganizerName : null),
                rec is null ? null : new MeetingRoomRecordingState(rec.Id, rec.Name ?? "", rec.StartedAt)));
        }
        return list;
    }

    /// Who is in the room per LiveKit, without the hidden Egress recorder; null = LiveKit not reachable for this room.
    private async Task<IReadOnlyList<LiveKitParticipant>?> ParticipantsAsync(string key, CancellationToken ct)
    {
        if (!liveKit.IsConfigured) return null;
        try
        {
            var ps = await liveKit.ListAsync(MeetingRoomKeys.LiveKitRoom(key), ct);
            return ps.Where(p => !p.Identity.StartsWith("EG_", StringComparison.Ordinal)).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// Display names come from the Users table by identity, not from the name a LiveKit client claims.
    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<LiveKitParticipant> participants, CancellationToken ct)
    {
        var ids = participants.Select(p => Guid.TryParse(p.Identity, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
    }

    private static List<MeetingRoomPerson> People(IReadOnlyList<LiveKitParticipant> ps, Dictionary<Guid, string> names) =>
        ps.Select(p =>
        {
            var id = Guid.TryParse(p.Identity, out var g) ? g : Guid.Empty;
            return new MeetingRoomPerson(id, names.GetValueOrDefault(id) ?? p.Name,
                p.Tracks.Any(t => !t.Muted && t.IsSource("SCREEN_SHARE")),
                p.Attributes.TryGetValue("rec", out var rec) && rec == "pc");
        }).ToList();

    public async Task<MeetingRoomJoinResult> JoinAsync(Guid userId, string key, CancellationToken ct)
    {
        var room = await db.HallRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);
        if (room is null) return new(null, "unknown_room", []);
        if (!tokens.IsConfigured || !liveKit.IsConfigured) return new(null, "media_unavailable", []);
        var me = userId.ToString();
        try
        {
            var inside = await liveKit.ListAsync(MeetingRoomKeys.LiveKitRoom(key), ct);
            var others = inside.Count(p => p.Identity != me && !p.Identity.StartsWith("EG_", StringComparison.Ordinal));
            if (others >= room.Capacity)
            {
                var free = new List<string>();
                foreach (var r in await db.HallRooms.AsNoTracking().Where(r => r.Key != key).OrderBy(r => r.SortOrder).ToListAsync(ct))
                    if ((await liveKit.ListAsync(MeetingRoomKeys.LiveKitRoom(r.Key), ct)).Count(p => !p.Identity.StartsWith("EG_", StringComparison.Ordinal)) < r.Capacity)
                        free.Add(r.Key);
                return new(null, "room_full", free);
            }
            await liveKit.CreateRoomAsync(MeetingRoomKeys.LiveKitRoom(key), MeetingRoomKeys.LiveKitMaxParticipants, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new(null, "media_unavailable", []); }

        var video = (await VideoAsync(ct))[key];
        var name = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstAsync(ct);
        var lkRoom = MeetingRoomKeys.LiveKitRoom(key);
        var token = tokens.GenerateToken(lkRoom, me, name, TimeSpan.FromHours(6),
            video ? MeetingRoomKeys.VideoSources : MeetingRoomKeys.AudioSources, canUpdateOwnMetadata: true);
        var retention = await settings.GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct);
        return new(new MeetingRoomJoinDto(room.Key, room.Name, lkRoom, tokens.ServerUrl!, token, video, me, retention), null, []);
    }

    public async Task<IReadOnlyDictionary<string, bool>> VideoAsync(CancellationToken ct)
    {
        var saved = await settings.GetAsync(SettingKeys.MeetingRoomsVideo, new Dictionary<string, bool>(), ct);
        return HallBooking.Rooms.ToDictionary(r => r.Key, r => !saved.TryGetValue(r.Key, out var on) || on);
    }

    public async Task SetVideoAsync(string key, bool allowed, string? actorNik, CancellationToken ct)
    {
        var saved = new Dictionary<string, bool>(await VideoAsync(ct)) { [key] = allowed };
        await settings.SetAsync(SettingKeys.MeetingRoomsVideo, saved, actorNik, ct);
        if (!liveKit.IsConfigured) return;
        var sources = allowed ? MeetingRoomKeys.VideoSources : MeetingRoomKeys.AudioSources;
        var room = MeetingRoomKeys.LiveKitRoom(key);
        try
        {
            foreach (var p in await liveKit.ListAsync(room, ct))
                if (!p.Identity.StartsWith("EG_", StringComparison.Ordinal))
                {
                    try
                    {
                        await liveKit.SetSourcesAsync(room, p.Identity, sources, ct);
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { /* continue updating other participants */ }
                }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { /* saved; applies on next join */ }
    }
}
