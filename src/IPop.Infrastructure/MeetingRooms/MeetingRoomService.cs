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
/// Rooms are booking-only (v2 §1): who may enter is decided by <see cref="RoomAccess"/>.
public sealed class MeetingRoomService(IPopDbContext db, ILiveKitRoomAdmin liveKit, ILiveKitTokenService tokens, ISettings settings,
    RoomPasswordLimiter passwordLimiter, IRoomBookingWriter bookings)
    : IMeetingRoomService
{
    public const string PrivateTitle = "Booked";
    /// Durations the "Use now" sheet offers; a shorter one only when the next booking caps the room (then ≥ UseNowMinMinutes).
    public static readonly int[] UseNowDurations = [30, 60, 90];
    public const int UseNowMinMinutes = 15;

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

    public async Task<MeetingRoomJoinResult> JoinAsync(Guid userId, string key, string? password, CancellationToken ct)
    {
        var room = await db.HallRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);
        if (room is null) return new(null, "unknown_room", []);
        if (!tokens.IsConfigured || !liveKit.IsConfigured) return new(null, "media_unavailable", []);
        var me = userId.ToString();
        try
        {
            var inside = await liveKit.ListAsync(MeetingRoomKeys.LiveKitRoom(key), ct);
            var people = inside.Where(p => !p.Identity.StartsWith("EG_", StringComparison.Ordinal)).ToList();
            if (await AdmitAsync(userId, key, people.Count > 0, password, ct) is { } refused) return new(null, refused, []);
            var others = people.Count(p => p.Identity != me);
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
        var token = tokens.GenerateToken(lkRoom, me, name, MeetingRoomKeys.TokenTtl,
            video ? MeetingRoomKeys.VideoSources : MeetingRoomKeys.AudioSources, canUpdateOwnMetadata: true);
        var retention = await settings.GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct);
        return new(new MeetingRoomJoinDto(room.Key, room.Name, lkRoom, tokens.ServerUrl!, token, video, me, retention), null, []);
    }

    /// Null = may enter. The password is only looked at for outsiders; the right one admits a guest for this booking.
    private async Task<string?> AdmitAsync(Guid userId, string key, bool occupied, string? password, CancellationToken ct)
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
        var occupied = await ParticipantsAsync(key, ct) is { Count: > 0 };   // unreachable LiveKit: rely on the bookings alone
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
