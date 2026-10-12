using IPop.Application.Calls;
using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.MeetingRooms;

/// The five SJA Hall rooms as drop-in meeting rooms. LiveKit is the truth for who is inside.
/// Rooms are booking-only (v2 §1): who may enter is decided by <see cref="RoomAccess"/>.
public sealed class MeetingRoomService(IPopDbContext db, ILiveKitRoomAdmin liveKit, ILiveKitTokenService tokens, ISettings settings,
    RoomPasswordLimiter passwordLimiter, IRoomBookingWriter bookings, RoomOccupancyTracker? occupancyTracker = null,
    ILogger<MeetingRoomService>? log = null, MeetingRoomMedia? mediaRooms = null, ILiveKitEgress? egress = null)
    : IMeetingRoomService
{
    /// Singleton in the app (DI); a private one when a test does not pass it.
    private readonly RoomOccupancyTracker occupancy = occupancyTracker ?? new RoomOccupancyTracker();

    private readonly MeetingRoomMedia media = mediaRooms ?? MeetingRoomMedia.Default;

    public const string PrivateTitle = "Booked";
    /// Durations the "Use now" sheet offers; a shorter one only when the next booking caps the room (then ≥ UseNowMinMinutes).
    public static readonly int[] UseNowDurations = [30, 60, 90];
    public const int UseNowMinMinutes = 15;

    public async Task<IReadOnlyList<MeetingRoomDto>> ListAsync(Guid viewerId, CancellationToken ct)
    {
        var rooms = await db.HallRooms.AsNoTracking().OrderBy(r => r.SortOrder).ToListAsync(ct);
        // LiveKit first and in parallel (each call may take its 5 s timeout); one unreachable room only marks that room.
        var sessions = new Dictionary<string, Booking?>();
        foreach (var r in rooms)
            sessions[r.Key] = (await RoomAccess.BookingsAsync(db, r.Key, DateTime.UtcNow, occupancy.Snapshot(r.Key, DateTime.UtcNow), ct)).Current;
        var inside = await Task.WhenAll(rooms.Select(r => ListPeopleAsync(r.Key, sessions[r.Key], ct)));
        var names = await NamesAsync(inside.Where(x => x is not null).SelectMany(x => x!), ct);
        var video = await VideoAsync(ct);
        var now = DateTime.UtcNow;
        var recordings = await db.MeetingRecordings.AsNoTracking()
            .Where(r => r.Status == MeetingRecordingStatus.Recording)
            .Select(r => new { r.RoomKey, r.MeetingId, r.Id, r.StartedAt, Name = db.Users.Where(u => u.Id == r.StartedById).Select(u => u.Name).FirstOrDefault() })
            .ToListAsync(ct);
        var list = new List<MeetingRoomDto>();
        for (var i = 0; i < rooms.Count; i++)
        {
            var room = rooms[i];
            var available = inside[i] is not null;
            var people = available ? People(inside[i]!, names) : [];
            // Same rules as join (RoomAccess): the 10-minute early window and an overrunning occupied booking count.
            var access = await RoomAccess.ForAsync(db, room.Key, viewerId, now,
                available ? occupancy.Observe(room.Key, people.Count > 0, now) : RoomOccupancy.Empty, ct);
            var rec = recordings.FirstOrDefault(r => r.RoomKey == room.Key && r.MeetingId == access.Current?.MeetingId);
            list.Add(new MeetingRoomDto(room.Key, room.Name, room.Capacity, video[room.Key], available, people,
                access.Current is { } cur ? await BookingDtoAsync(cur, viewerId, ct) : null,
                access.Next is { } nxt ? await BookingDtoAsync(nxt, viewerId, ct) : null,
                rec is null ? null : new MeetingRoomRecordingState(rec.Id, rec.Name ?? "", rec.StartedAt),
                CardAccess(access, now)));
        }
        return list;
    }

    /// v2 §1.6 card state for the viewer.
    public static string CardAccess(RoomAccessInfo access, DateTime nowUtc) => access.Current switch
    {
        null => MeetingRoomAccess.Free,
        { } b when !RoomAccess.MayEnter(access.Role) => MeetingRoomAccess.Locked,
        { } b when nowUtc < b.StartAt => MeetingRoomAccess.Starting,
        _ => MeetingRoomAccess.Invited,
    };

    /// v1 privacy rule: title and organizer only for the organizer and non-declined attendees (not for guests).
    private async Task<MeetingRoomBookingDto> BookingDtoAsync(Booking b, Guid viewerId, CancellationToken ct)
    {
        var role = await RoomAccess.RoleAsync(db, b, viewerId, ct);
        var own = role is "organizer" or "invited";
        var organizer = own ? await db.Users.AsNoTracking().Where(u => u.Id == b.OrganizerId).Select(u => u.Name).FirstOrDefaultAsync(ct) : null;
        return new MeetingRoomBookingDto(b.MeetingId, own ? b.Title : PrivateTitle, Utc(b.StartAt), Utc(b.EndAt), organizer,
            b.OrganizerId == viewerId);
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);

    public static readonly TimeSpan ExtendBy = TimeSpan.FromMinutes(15);

    public async Task<(Guid? MeetingId, DateTime? EndAt, string? Error)> ExtendAsync(Guid userId, string key, CancellationToken ct)
    {
        if (!HallBooking.Rooms.Any(r => r.Key == key)) return (null, null, "unknown_room");
        var now = DateTime.UtcNow;
        var inside = await ParticipantsAsync(key, ct);
        now = DateTime.UtcNow;   // after the LiveKit listing returned
        var access = await RoomAccess.ForAsync(db, key, userId, now,
            inside is null ? RoomOccupancy.Empty : occupancy.Observe(key, inside.Count > 0, now), ct);
        // Without LiveKit an overrun cannot be told apart from an ended booking: say so instead of "no booking".
        if (access.Current is not { } booking) return (null, null, inside is null ? "media_unavailable" : "no_booking");
        if (booking.OrganizerId != userId) return (null, null, "forbidden");
        // RoomAccess already caps an overrun at 4 h and ignores bookings from before today; checked again here because
        // this path rewrites the meeting.
        if (now >= Utc(booking.EndAt) + RoomAccess.MaxOverrun || Utc(booking.EndAt) <= RoomAccess.DayStartUtc(now))
            return (null, null, "no_booking");
        // From the later of the booked end and now: an overrunning meeting gets 15 minutes from now.
        var baseEnd = Utc(booking.EndAt) > now ? Utc(booking.EndAt) : new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var end = baseEnd + ExtendBy;
        if (access.Next is { } next && end > RoomAccess.WindowStart(next)) return (null, null, "room_booked");
        var error = await bookings.SetEndAsync(userId, booking.MeetingId, end, ct);
        if (error is not null) return (null, null, error == "not_found" ? "no_booking" : error);
        await PublishMetadataSafeAsync(key, ct);
        return (booking.MeetingId, end, null);
    }

    /// Room metadata after a join or an extend. Never turns a successful action into an error: anything but
    /// cancellation is logged and the booking worker writes it again within 30 s.
    private async Task PublishMetadataSafeAsync(string key, CancellationToken ct)
    {
        try { await RoomMetadata.PublishAsync(db, liveKit, occupancy, key, DateTime.UtcNow, ct, mediaRooms: media); }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(e, "Meeting room {Room}: metadata update failed", key);
        }
    }

    /// Who is in the room per LiveKit, without the hidden Egress recorder; null = LiveKit not reachable for this room.
    private async Task<IReadOnlyList<LiveKitParticipant>?> ParticipantsAsync(string key, CancellationToken ct)
    {
        if (!liveKit.IsConfigured) return null;
        try { return (await BookingMediaSession.ObserveAsync(db, liveKit, occupancy, media, key, DateTime.UtcNow, ct)).People; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { occupancy.ObserveFailed(key); return null; }
    }

    private async Task<IReadOnlyList<LiveKitParticipant>?> ListPeopleAsync(string key, Booking? booking, CancellationToken ct)
    {
        if (!liveKit.IsConfigured) return null;
        if (booking is null) return [];
        try { return (await liveKit.ListAsync(media.Room(key, booking.MeetingId), ct)).Where(p => !p.Identity.StartsWith("EG_", StringComparison.Ordinal)).ToList(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { occupancy.ObserveFailed(key); return null; }
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

    public async Task<MeetingRoomJoinResult> JoinAsync(Guid userId, string key, string? password, CancellationToken ct)
    {
        var room = await db.HallRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);
        if (room is null) return new(null, "unknown_room", []);
        if (!tokens.IsConfigured || !liveKit.IsConfigured) return new(null, "media_unavailable", []);
        var me = userId.ToString();
        Booking? admitted;
        try
        {
            var session = await BookingMediaSession.ObserveAsync(db, liveKit, occupancy, media, key, DateTime.UtcNow, ct);
            admitted = session.Booking;
            var people = session.People;
            if (admitted is null) return new(null, "no_booking", []);
            if (!await RecordingBoundary.EndPreviousAsync(db, egress, media, key, admitted.MeetingId, DateTime.UtcNow, ct))
                return new(null, "media_unavailable", []);
            if (await AdmitAsync(userId, key, occupancy.Observe(key, people.Count > 0, DateTime.UtcNow), password, ct) is { } refused) return new(null, refused, []);
            var others = people.Count(p => p.Identity != me);
            if (others >= room.Capacity)
            {
                var free = new List<string>();
                foreach (var r in await db.HallRooms.AsNoTracking().Where(r => r.Key != key).OrderBy(r => r.SortOrder).ToListAsync(ct))
                    if ((await ParticipantsAsync(r.Key, ct)) is { } ps && ps.Count < r.Capacity)
                        free.Add(r.Key);
                return new(null, "room_full", free);
            }
            await liveKit.CreateRoomAsync(media.Room(key, admitted.MeetingId), MeetingRoomKeys.LiveKitMaxParticipants, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new(null, "media_unavailable", []); }

        var video = (await VideoAsync(ct))[key];
        var name = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstAsync(ct);
        var final = (await RoomAccess.BookingsAsync(db, key, DateTime.UtcNow, occupancy.Snapshot(key, DateTime.UtcNow), ct)).Current;
        if (final?.MeetingId != admitted.MeetingId) return new(null, "no_booking", []);
        var lkRoom = media.Room(key, admitted.MeetingId);
        var token = tokens.GenerateToken(lkRoom, me, name, MeetingRoomKeys.TokenTtl,
            video ? MeetingRoomKeys.VideoSources : MeetingRoomKeys.AudioSources, canUpdateOwnMetadata: true);
        var retention = await settings.GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct);
        // Fresh booking info (endAt / nextStartAt) for the room before the client connects; the worker keeps it current.
        await PublishMetadataSafeAsync(key, ct);
        return new(new MeetingRoomJoinDto(room.Key, room.Name, lkRoom, tokens.ServerUrl!, token, video, me, retention), null, []);
    }

    /// Null = may enter. The password is only looked at for outsiders; the right one admits a guest for this booking.
    private async Task<string?> AdmitAsync(Guid userId, string key, RoomOccupancy occupied, string? password, CancellationToken ct)
    {
        var access = await RoomAccess.ForAsync(db, key, userId, DateTime.UtcNow, occupied, ct);
        if (access.Current is not { } booking) return "no_booking";
        if (RoomAccess.MayEnter(access.Role)) return null;
        if (string.IsNullOrWhiteSpace(password)) return "password_required";
        // Reserve the attempt before comparing (atomic); give it back only when the password was right.
        if (!passwordLimiter.TryConsume(userId, booking.MeetingId)) return "too_many_attempts";
        if (!RoomPasswords.Matches(booking.Password, password)) return "wrong_password";
        passwordLimiter.Refund(userId, booking.MeetingId);
        db.MeetingRoomGuests.Add(new MeetingRoomGuest { MeetingId = booking.MeetingId, UserId = userId, AdmittedAt = DateTime.UtcNow });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }   // a parallel join admitted the same guest first
        return null;
    }

    public async Task<(Guid? MeetingId, string? Error, int? MaxMinutes)> UseNowAsync(Guid userId, string key, int minutes, string? title,
        IReadOnlyList<string> attendeeNiks, CancellationToken ct)
    {
        var room = await db.HallRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);
        if (room is null) return (null, "unknown_room", null);
        if (minutes < UseNowMinMinutes || minutes > UseNowDurations.Max()) return (null, "invalid_minutes", null);
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        // Unreachable LiveKit: rely on the bookings alone.
        var occupied = await ParticipantsAsync(key, ct) is { } inside ? occupancy.Observe(key, inside.Count > 0, DateTime.UtcNow) : RoomOccupancy.Empty;
        var access = await RoomAccess.ForAsync(db, key, userId, now, occupied, ct);
        if (access.Current is not null) return (null, "room_booked", null);
        // A booking that ended earlier in this minute: start at its end, not at the floored minute (no overlap with it).
        var loc = HallBooking.Location(room.Name);
        var justEnded = await db.Meetings.AsNoTracking()
            .Where(m => m.Status != MeetingStatus.Cancelled && m.Location == loc && m.EndAt > start && m.EndAt <= now)
            .MaxAsync(m => (DateTime?)m.EndAt, ct);
        if (justEnded is { } ended && ended > start) start = DateTime.SpecifyKind(ended, DateTimeKind.Utc);

        // Never into the next booking's lock window (Review Focus 4).
        int? cap = access.Next is { } next ? (int)Math.Floor((next.StartAt - RoomAccess.Early - start).TotalMinutes) : null;
        int? offer = cap is >= UseNowMinMinutes ? cap : null;
        if (cap is { } limit && minutes > limit) return (null, "room_booked", offer);
        // 30/60/90 only; another value (≥ 15, checked above) only when the next booking caps the room below 90.
        if (!UseNowDurations.Contains(minutes) && cap is not < 90) return (null, "invalid_minutes", null);
        var name = string.IsNullOrWhiteSpace(title) ? $"{room.Name} meeting" : title.Trim();
        if (name.Length > 200) name = name[..200];
        var id = await bookings.CreateAsync(userId, name, start, start.AddMinutes(minutes), HallBooking.Location(room.Name),
            attendeeNiks ?? [], ct);
        return id is null ? (null, "room_booked", null) : (id, null, null);
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
        var booking = (await RoomAccess.BookingsAsync(db, key, DateTime.UtcNow, occupancy.Snapshot(key, DateTime.UtcNow), ct)).Current;
        if (booking is null) return;
        var room = media.Room(key, booking.MeetingId);
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
