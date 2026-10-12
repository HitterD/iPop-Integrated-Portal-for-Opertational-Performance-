using System.Text.Json;
using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.MeetingRooms;

/// In-meeting chat (meeting rooms v2 §3): messages go through the API (stored per booking, linked to a running server
/// recording) and are then relayed to the LiveKit room on topic <see cref="MeetingChat.Topic"/>.
public sealed class MeetingChatService(IPopDbContext db, ILiveKitRoomAdmin liveKit, MeetingChatRateLimiter limiter,
    RoomOccupancyTracker? occupancyTracker = null, ILogger<MeetingChatService>? log = null, MeetingRoomMedia? mediaRooms = null, TimeProvider? timeProvider = null) : IMeetingChatService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RoomOccupancyTracker occupancy = occupancyTracker ?? new RoomOccupancyTracker();
    private readonly MeetingRoomMedia media = mediaRooms ?? MeetingRoomMedia.Default;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<MeetingChatResult> SendAsync(Guid userId, string key, string? text, CancellationToken ct)
    {
        var body = text?.Trim() ?? "";
        if (body.Length == 0) return new(null, "empty");
        if (body.Length > MeetingChat.MaxLength) return new(null, "too_long");
        if (!IsRoom(key)) return new(null, "unknown_room");
        if (!liveKit.IsConfigured) return new(null, "media_unavailable");
        (Booking? Booking, IReadOnlyList<LiveKitParticipant> People) session;
        try { session = await BookingMediaSession.ObserveAsync(db, liveKit, occupancy, media, key, clock.GetUtcNow().UtcDateTime, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new(null, "media_unavailable"); }
        if (session.Booking is null) return new(null, "no_booking");
        var inside = session.People;
        var now = clock.GetUtcNow().UtcDateTime;
        var access = await RoomAccess.ForAsync(db, key, userId, now, occupancy.Snapshot(key, now), ct);
        if (access.Current is null) return new(null, "no_booking");
        if (access.Current.MeetingId != session.Booking.MeetingId) return new(null, "not_in_room");
        if (inside.All(p => p.Identity != userId.ToString())) return new(null, "not_in_room");
        if (!RoomAccess.MayEnter(access.Role)) return new(null, "forbidden");
        if (!limiter.TryAcquire(userId, now)) return new(null, "rate_limited");

        var name = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "";
        var rec = await db.MeetingRecordings.AsNoTracking().Where(r => r.RoomKey == key && r.MeetingId == access.Current.MeetingId && r.Status == MeetingRecordingStatus.Recording)
            .Select(r => new { r.Id, r.StartedAt }).FirstOrDefaultAsync(ct);
        var row = new MeetingChatMessage
        {
            Id = Guid.NewGuid(), MeetingId = access.Current.MeetingId, RoomKey = key, UserId = userId, Kind = MeetingChat.KindText,
            Text = body, SentAt = now, RecordingId = rec?.Id, OffsetSeconds = rec is null ? null : Offset(now, rec.StartedAt),
        };
        db.MeetingChatMessages.Add(row);
        await db.SaveChangesAsync(ct);
        var dto = Dto(row, name);
        await RelayAsync(liveKit, media.Room(key, access.Current.MeetingId), dto, log, ct);
        return new(dto, null);
    }

    public async Task<(IReadOnlyList<MeetingChatMessageDto>? Messages, string? Error)> HistoryAsync(Guid userId, string key, CancellationToken ct)
    {
        if (!IsRoom(key)) return (null, "unknown_room");
        IReadOnlyList<LiveKitParticipant> inside = [];
        if (liveKit.IsConfigured)
        {
            try { inside = (await BookingMediaSession.ObserveAsync(db, liveKit, occupancy, media, key, DateTime.UtcNow, ct)).People; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (null, "media_unavailable"); }
        }
        var access = await AccessAsync(key, userId, inside, DateTime.UtcNow, ct);
        if (access.Current is null) return (null, "no_booking");
        if (!RoomAccess.MayEnter(access.Role)) return (null, "forbidden");
        var meetingId = access.Current.MeetingId;
        // The newest 500, shown oldest first.
        var rows = await Query(db.MeetingChatMessages.AsNoTracking().Where(m => m.MeetingId == meetingId)
            .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).Take(MeetingChat.HistoryLimit)).ToListAsync(ct);
        return (rows.OrderBy(m => m.SentAt).ThenBy(m => m.Id).ToList(), null);
    }

    public async Task<IReadOnlyList<MeetingChatMessageDto>?> RecordingChatAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct)
    {
        // Same rule as MeetingRecordingService.CanViewAsync: people who were in the room while it recorded, and Admins.
        if (!await db.MeetingRecordings.AnyAsync(r => r.Id == recordingId && r.Status != MeetingRecordingStatus.Deleted
                && (admin || r.Viewers.Any(v => v.UserId == userId)), ct)) return null;
        var rows = await Query(db.MeetingChatMessages.AsNoTracking().Where(m => m.RecordingId == recordingId && db.MeetingRecordings.Any(r => r.Id == recordingId && r.MeetingId == m.MeetingId))).ToListAsync(ct);
        return rows.OrderBy(m => m.OffsetSeconds).ThenBy(m => m.SentAt).ThenBy(m => m.Id).ToList();
    }

    /// Writes a system line ("X started recording", "Recording stopped") into the booking's chat, linked to the recording,
    /// and relays it like a text message. Best effort: the recording itself never fails because of its chat line.
    public static async Task PostSystemAsync(IPopDbContext db, ILiveKitRoomAdmin liveKit, MeetingRecording recording, string text,
        DateTime nowUtc, ILogger? log, CancellationToken ct, MeetingRoomMedia? mediaRooms = null)
    {
        if (recording.MeetingId is not { } meetingId) return;   // ad-hoc recording: there is no booking chat
        try
        {
            var row = new MeetingChatMessage
            {
                Id = Guid.NewGuid(), MeetingId = meetingId, RoomKey = recording.RoomKey, UserId = Guid.Empty, Kind = MeetingChat.KindSystem,
                Text = text, SentAt = nowUtc, RecordingId = recording.Id, OffsetSeconds = Offset(nowUtc, recording.StartedAt),
            };
            db.MeetingChatMessages.Add(row);
            await db.SaveChangesAsync(ct);
            await RelayAsync(liveKit, (mediaRooms ?? MeetingRoomMedia.Default).Room(recording.RoomKey, meetingId), Dto(row, ""), log, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(e, "Meeting room {Room}: recording chat line failed", recording.RoomKey);
        }
    }

    /// Payload on topic ipop.chat: { id, userId, name, text, sentAt, kind } (UTF-8 JSON). Never a NIK.
    public static byte[] Payload(MeetingChatMessageDto m) => JsonSerializer.SerializeToUtf8Bytes(
        new { id = m.Id, userId = m.UserId, name = m.Name, text = m.Text, sentAt = m.SentAt, kind = m.Kind }, Json);

    private static async Task RelayAsync(ILiveKitRoomAdmin liveKit, string key, MeetingChatMessageDto dto, ILogger? log, CancellationToken ct)
    {
        // The message is stored; the sender renders it from the response and the others reload history on (re)join.
        try { await liveKit.SendDataAsync(key, Payload(dto), MeetingChat.Topic, ct); }
        catch (Exception e) { log?.LogWarning(e, "Meeting room {Room}: chat relay failed", key); }
    }

    private async Task<RoomAccessInfo> AccessAsync(string key, Guid userId, IReadOnlyList<LiveKitParticipant> inside, DateTime now, CancellationToken ct)
    {
        var occupied = occupancy.Observe(key, inside.Any(p => !p.Identity.StartsWith("EG_", StringComparison.Ordinal)), now);
        return await RoomAccess.ForAsync(db, key, userId, now, occupied, ct);
    }

    private IQueryable<MeetingChatMessageDto> Query(IQueryable<MeetingChatMessage> q) => q.Select(m => new MeetingChatMessageDto(
        m.Id, m.UserId, db.Users.Where(u => u.Id == m.UserId).Select(u => u.Name).FirstOrDefault() ?? "", m.Text, m.SentAt, m.Kind, m.OffsetSeconds));

    private static MeetingChatMessageDto Dto(MeetingChatMessage m, string name) =>
        new(m.Id, m.UserId, name, m.Text, m.SentAt, m.Kind, m.OffsetSeconds);

    private static int Offset(DateTime at, DateTime startedAt) => Math.Max(0, (int)(at - startedAt).TotalSeconds);

    private static bool IsRoom(string key) => HallBooking.Rooms.Any(r => r.Key == key);
}
