using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Repeating events. Every occurrence is a normal event (own meeting, RSVPs and reminders) carrying the
/// series id, so the feed, reminders, sharing and RSVP work unchanged. Editing or cancelling one asks for
/// a scope: this occurrence, this and the following ones, or the whole series.
public static partial class CalendarEndpoints
{
    /// Creates one event, or a series when <see cref="SaveEventRequest.Recurrence"/> is set. Returns the first
    /// occurrence and who was invited (they get one invite for the series, not one per occurrence).
    public static async Task<(SaveResult, CalendarEvent?, IReadOnlyList<Guid>)> CreateSeriesAsync(IPopDbContext db, Guid userId, SaveEventRequest req,
        CancellationToken ct, Guid? actedBy = null, SaveIssue? issue = null)
    {
        var rule = CalendarRecurrence.Parse(req.Recurrence);
        if (rule is null) return string.IsNullOrWhiteSpace(req.Recurrence) ? await CreateEventAsync(db, userId, req with { Recurrence = null }, ct, actedBy, issue) : (SaveResult.InvalidRange, null, []);

        var (r, _, start, end) = Normalize(req);
        if (r != SaveResult.Ok) return (r, null, []);
        var tz = await ZoneOfAsync(db, userId, ct);
        var length = TimeZoneInfo.ConvertTimeFromUtc(end, tz) - TimeZoneInfo.ConvertTimeFromUtc(start, tz);
        if (length <= TimeSpan.Zero) return (SaveResult.InvalidRange, null, []);
        var seriesId = Guid.NewGuid();
        CalendarEvent? first = null;
        IReadOnlyList<Guid> invited = [];
        var overrides = (req.Overrides ?? []).GroupBy(o => o.Date).ToDictionary(g => g.Key, g => g.Last());
        var occurrences = Occurrences(rule, start, length, tz)
            .Where(o => !(overrides.TryGetValue(o.Date, out var ov) && ov.Skip)).ToList();
        if (occurrences.Count == 0) return (overrides.Count > 0 ? SaveResult.NothingToSave : SaveResult.InvalidRange, null, []);
        // Only the first date of a room series has to be within the 60-day limit (v3 mockup screen 3); later ones still meet
        // every other limit (length, same day) through CreateEventAsync.
        var firstStart = occurrences[0].Start;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var (date, at, finish) in occurrences)
        {
                var one = req with { StartAt = at, EndAt = finish, Recurrence = null, Overrides = null };
                if (overrides.TryGetValue(date, out var ov) && ov.RoomKey is { } key)
                {
                    var room = IPop.Application.Hall.HallBooking.Rooms.FirstOrDefault(r => r.Key == key).Name;
                    if (room is null) { await transaction.RollbackAsync(ct); db.ChangeTracker.Clear(); if (issue is not null) issue.Date = date; return (SaveResult.RoomRequired, null, []); }
                    one = one with { Location = IPop.Application.Hall.HallBooking.Location(room), Kind = MeetingKind.Room };
                }
                var (res, ev, inv) = await CreateEventAsync(db, userId, one, ct, actedBy, issue, limitsFrom: at == firstStart ? null : firstStart);
                if (res != SaveResult.Ok && issue is not null) issue.Date = date;
                if (res != SaveResult.Ok) { await transaction.RollbackAsync(ct); db.ChangeTracker.Clear(); return (res, null, []); }
                ev!.SeriesId = seriesId;
                ev.Recurrence = rule.ToString();
                first ??= ev;
                if (invited.Count == 0) invited = inv;
        }
        if (first is null) return (SaveResult.InvalidRange, null, []);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (SaveResult.Ok, first, invited);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
    }

    /// Every occurrence of a new series: local date, UTC start and end (the local length is kept across DST changes).
    public static List<(DateOnly Date, DateTime Start, DateTime End)> Occurrences(CalendarRecurrence rule, DateTime startUtc, TimeSpan localLength,
        TimeZoneInfo tz)
    {
        var list = new List<(DateOnly, DateTime, DateTime)>();
        foreach (var at in rule.Expand(startUtc, tz))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(at, tz);
            if (!CalendarRecurrence.TryLocalToUtc(local + localLength, tz, out var finish)) continue;
            list.Add((DateOnly.FromDateTime(local), at, finish));
        }
        return list;
    }

    public const int MaxPreviewOccurrences = CalendarRecurrence.MaxOccurrences;

    /// Preview of a repeating room booking (v3 §3) for <paramref name="userId"/>. Errors: "unknown_room", "invalid_recurrence",
    /// "too_many" (more than 100 dates), or a RoomBookingRules code for the first date.
    public static async Task<(string? Error, SeriesPreviewDto? Preview)> PreviewRoomSeriesAsync(IPopDbContext db, Guid userId, SeriesPreviewRequest req,
        CancellationToken ct)
    {
        var name = IPop.Application.Hall.HallBooking.Rooms.FirstOrDefault(r => r.Key == req.RoomKey).Name;
        if (name is null) return ("unknown_room", null);
        var rule = CalendarRecurrence.Parse(req.Recurrence);
        if (rule is null) return ("invalid_recurrence", null);
        var (start, end) = (AsUtc(req.StartAt), AsUtc(req.EndAt));
        if (await MeetingEndpoints.RoomRangeAsync(db, userId, start, end, false, ct) is { } bad) return (bad, null);
        var tz = await ZoneOfAsync(db, userId, ct);
        var occurrences = Occurrences(rule, start, TimeZoneInfo.ConvertTimeFromUtc(end, tz) - TimeZoneInfo.ConvertTimeFromUtc(start, tz), tz);
        if (occurrences.Count > MaxPreviewOccurrences) return ("too_many", null);
        if (occurrences.Count == 0) return ("invalid_recurrence", null);

        var exceptMeetings = req.ExceptSeriesId is { } sid
            ? await db.CalendarEvents.AsNoTracking().Where(e => e.SeriesId == sid && e.MeetingId != null).Select(e => e.MeetingId!.Value).ToListAsync(ct)
            : [];
        var locations = IPop.Application.Hall.HallBooking.Rooms.Select(r => IPop.Application.Hall.HallBooking.Location(r.Name)).ToList();
        var (from, to) = (occurrences[0].Start, occurrences[^1].End);
        var busy = await db.Meetings.AsNoTracking()
            .Where(m => m.Status != MeetingStatus.Cancelled && m.Location != null && locations.Contains(m.Location) && !exceptMeetings.Contains(m.Id)
                && m.StartAt < to && from < m.EndAt)
            .OrderBy(m => m.StartAt)
            .Select(m => new
            {
                m.Location, m.Title, m.StartAt, m.EndAt,
                Visible = m.OrganizerId == userId || m.Attendees.Any(a => a.UserId == userId && a.RsvpStatus != "decline"),
            })
            .ToListAsync(ct);
        var own = IPop.Application.Hall.HallBooking.Location(name);
        var list = new List<SeriesOccurrenceDto>();
        foreach (var (date, s, e) in occurrences)
        {
            var hits = busy.Where(b => b.StartAt < e && s < b.EndAt).ToList();
            var clash = hits.FirstOrDefault(b => b.Location == own);
            var free = IPop.Application.Hall.HallBooking.Rooms
                .Where(r => r.Key != req.RoomKey && hits.All(b => b.Location != IPop.Application.Hall.HallBooking.Location(r.Name)))
                .Select(r => r.Key).ToList();
            list.Add(new SeriesOccurrenceDto(date, s, e, clash is null,
                clash is null ? null : clash.Visible ? clash.Title : IPop.Infrastructure.MeetingRooms.MeetingRoomService.PrivateTitle, free,
                clash is null ? null : "booked"));
        }
        return (null, new SeriesPreviewDto(list.Count, list.Count(o => !o.Free), list));
    }

    /// POST /api/calendar/series/preview: 200 SeriesPreviewDto; 404 unknown_room; 400 invalid_recurrence | too_many |
    /// invalid_range (+ reason).
    public static async Task<IResult> PreviewAsync(SeriesPreviewRequest req, HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(http, db, ct);
        if (userId is null) return Results.Unauthorized();
        var (error, preview) = await PreviewRoomSeriesAsync(db, userId.Value, req, ct);
        return error switch
        {
            null => Results.Ok(preview),
            "unknown_room" => Results.Json(new { code = error, message = "That room does not exist." }, statusCode: 404),
            "invalid_recurrence" => Results.Json(new { code = error, message = "Choose a valid repeat rule." }, statusCode: 400),
            "too_many" => Results.Json(new { code = error, message = $"A series can have at most {MaxPreviewOccurrences} dates." }, statusCode: 400),
            _ => MeetingRoomEndpoints.RangeError(error),
        };
    }

    /// The occurrences a scope covers, in order. "this" = just the one; "following" = from it on; "all" = whole series.
    private static async Task<List<CalendarEvent>> ScopeAsync(IPopDbContext db, CalendarEvent anchor, string scope, CancellationToken ct)
    {
        if (anchor.SeriesId is not { } sid || scope == "this") return [anchor];
        var q = db.CalendarEvents.Where(e => e.SeriesId == sid && e.OwnerId == anchor.OwnerId);
        if (scope == "following") q = q.Where(e => e.StartAt >= anchor.StartAt);
        return await q.OrderBy(e => e.StartAt).ToListAsync(ct);
    }

    /// Applies the form to every occurrence in scope, keeping each one's own date but taking the new time of
    /// day, length and details. Returns the occurrence to show and everyone affected (for live refresh).
    public static async Task<(SaveResult, Guid FirstId, IReadOnlyList<Guid> Touched)> UpdateSeriesAsync(IPopDbContext db, Guid id, Guid userId,
        SaveEventRequest req, string scope, CancellationToken ct, Guid? actedBy = null, Dictionary<Guid, List<Guid>>? invitations = null,
        SaveIssue? issue = null)
    {
        var anchor = await db.CalendarEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (anchor is null) return (SaveResult.NotFound, id, []);
        if (anchor.OwnerId != userId) return (SaveResult.Forbidden, id, []);
        var (r, _, start, end) = Normalize(req);
        if (r != SaveResult.Ok) return (r, id, []);
        var tz = await ZoneOfAsync(db, userId, ct);
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(start, tz);
        var shift = localStart - TimeZoneInfo.ConvertTimeFromUtc(anchor.StartAt, tz);
        var length = TimeZoneInfo.ConvertTimeFromUtc(end, tz) - localStart;
        if (length <= TimeSpan.Zero) return (SaveResult.InvalidRange, id, []);
        var touched = new HashSet<Guid>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var occurrences = await ScopeAsync(db, anchor, scope, ct);
            if (!await CanMutateScopeAsync(db, occurrences, userId, actedBy, ct)) return (SaveResult.Forbidden, id, []);
            var pendingInvites = new Dictionary<Guid, List<Guid>>();
            // Room limits: 60 days count from the first date in scope (as for a new series).
            DateTime? scopeFirst = null;
            if (occurrences.Count > 1
                && CalendarRecurrence.TryLocalToUtc(TimeZoneInfo.ConvertTimeFromUtc(occurrences[0].StartAt, tz) + shift, tz, out var firstUtc))
                scopeFirst = firstUtc;
            foreach (var ev in occurrences)
        {
                if (ev.MeetingId is { } before)
                    foreach (var a in await db.MeetingAttendees.AsNoTracking().Where(x => x.MeetingId == before).Select(x => x.UserId).ToListAsync(ct)) touched.Add(a);
                var local = TimeZoneInfo.ConvertTimeFromUtc(ev.StartAt, tz) + shift;
                if (!CalendarRecurrence.TryLocalToUtc(local, tz, out var s) || !CalendarRecurrence.TryLocalToUtc(local + length, tz, out var finish)) continue;
                var (res, saved, invited) = await UpdateEventAsync(db, ev.Id, userId, req with { StartAt = s, EndAt = finish, Recurrence = null, Overrides = null }, ct,
                    actedBy, issue, scopeFirst);
                if (res is not (SaveResult.Ok or SaveResult.Cancelled) && issue is not null) issue.Date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(s, tz));
                if (res is SaveResult.Cancelled) continue;
                if (res != SaveResult.Ok) { await transaction.RollbackAsync(ct); db.ChangeTracker.Clear(); return (res, id, []); }
                if (invited.Count > 0) pendingInvites[ev.Id] = invited.ToList();
                foreach (var i in invited) touched.Add(i);
                if (saved?.MeetingId is { } mid)
                    foreach (var a in await db.MeetingAttendees.AsNoTracking().Where(x => x.MeetingId == mid).Select(x => x.UserId).ToListAsync(ct)) touched.Add(a);
        }
        await transaction.CommitAsync(ct);
        if (invitations is not null) foreach (var pair in pendingInvites) invitations[pair.Key] = pair.Value;
        return (SaveResult.Ok, id, touched.ToList());
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
    }

    public static async Task<(SaveResult, string Title, IReadOnlyList<Guid> People, int Count)> DeleteSeriesAsync(IPopDbContext db, Guid id, Guid userId,
        string scope, CancellationToken ct, Guid? actedBy = null)
    {
        var anchor = await db.CalendarEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (anchor is null) return (SaveResult.NotFound, "", [], 0);
        if (anchor.OwnerId != userId) return (SaveResult.Forbidden, "", [], 0);
        var people = new HashSet<Guid>();
        var count = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var occurrences = await ScopeAsync(db, anchor, scope, ct);
            if (!await CanMutateScopeAsync(db, occurrences, userId, actedBy, ct)) return (SaveResult.Forbidden, "", [], 0);
            foreach (var ev in occurrences)
        {
                var (res, _, attendees) = await DeleteEventAsync(db, ev.Id, userId, ct);
                if (res != SaveResult.Ok) { await transaction.RollbackAsync(ct); db.ChangeTracker.Clear(); return (res, "", [], 0); }
                if (res == SaveResult.Ok) { count++; foreach (var a in attendees) people.Add(a); }
        }
        await transaction.CommitAsync(ct);
        return (SaveResult.Ok, anchor.Title, people.ToList(), count);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
    }

    private static async Task<bool> CanMutateScopeAsync(IPopDbContext db, IEnumerable<CalendarEvent> occurrences, Guid ownerId, Guid? actedBy, CancellationToken ct)
    {
        if (actedBy is not { } actor || actor == ownerId) return true;
        foreach (var ev in occurrences)
        {
            var (owner, _) = await CalendarDelegateEndpoints.ActorForEventAsync(db, ev.Id, actor, ct);
            if (owner != ownerId) return false;
        }
        return true;
    }

    private static async Task<TimeZoneInfo> ZoneOfAsync(IPopDbContext db, Guid userId, CancellationToken ct) =>
        CalendarSharingEndpoints.ResolveTimeZone(await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZoneId).FirstOrDefaultAsync(ct));
}
