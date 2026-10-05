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
        CancellationToken ct, Guid? actedBy = null)
    {
        var rule = CalendarRecurrence.Parse(req.Recurrence);
        if (rule is null) return await CreateEventAsync(db, userId, req with { Recurrence = null }, ct, actedBy);

        var (r, _, start, end) = Normalize(req);
        if (r != SaveResult.Ok) return (r, null, []);
        var tz = await ZoneOfAsync(db, userId, ct);
        var length = end - start;
        var seriesId = Guid.NewGuid();
        CalendarEvent? first = null;
        IReadOnlyList<Guid> invited = [];
        foreach (var at in rule.Expand(start, tz))
        {
            var (res, ev, inv) = await CreateEventAsync(db, userId, req with { StartAt = at, EndAt = at + length, Recurrence = null }, ct, actedBy);
            if (res != SaveResult.Ok) return (res, null, []);
            ev!.SeriesId = seriesId;
            ev.Recurrence = rule.ToString();
            first ??= ev;
            if (invited.Count == 0) invited = inv;
        }
        await db.SaveChangesAsync(ct);
        return (SaveResult.Ok, first, invited);
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
        SaveEventRequest req, string scope, CancellationToken ct, Guid? actedBy = null)
    {
        var anchor = await db.CalendarEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (anchor is null) return (SaveResult.NotFound, id, []);
        if (anchor.OwnerId != userId) return (SaveResult.Forbidden, id, []);
        var (r, _, start, end) = Normalize(req);
        if (r != SaveResult.Ok) return (r, id, []);
        var tz = await ZoneOfAsync(db, userId, ct);
        var shift = TimeZoneInfo.ConvertTimeFromUtc(start, tz).TimeOfDay - TimeZoneInfo.ConvertTimeFromUtc(anchor.StartAt, tz).TimeOfDay;
        var dayShift = TimeZoneInfo.ConvertTimeFromUtc(start, tz).Date - TimeZoneInfo.ConvertTimeFromUtc(anchor.StartAt, tz).Date;
        var length = end - start;
        var touched = new HashSet<Guid>();
        foreach (var ev in await ScopeAsync(db, anchor, scope, ct))
        {
            var s = ev.StartAt + dayShift + shift;
            var (res, saved, invited) = await UpdateEventAsync(db, ev.Id, userId, req with { StartAt = s, EndAt = s + length, Recurrence = null }, ct, actedBy);
            if (res is SaveResult.Cancelled) continue;
            if (res != SaveResult.Ok) return (res, id, []);
            foreach (var i in invited) touched.Add(i);
            if (saved?.MeetingId is { } mid)
                foreach (var a in await db.MeetingAttendees.AsNoTracking().Where(x => x.MeetingId == mid).Select(x => x.UserId).ToListAsync(ct)) touched.Add(a);
        }
        return (SaveResult.Ok, id, touched.ToList());
    }

    public static async Task<(SaveResult, string Title, IReadOnlyList<Guid> People, int Count)> DeleteSeriesAsync(IPopDbContext db, Guid id, Guid userId,
        string scope, CancellationToken ct)
    {
        var anchor = await db.CalendarEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (anchor is null) return (SaveResult.NotFound, "", [], 0);
        if (anchor.OwnerId != userId) return (SaveResult.Forbidden, "", [], 0);
        var people = new HashSet<Guid>();
        var count = 0;
        foreach (var ev in await ScopeAsync(db, anchor, scope, ct))
        {
            var (res, _, attendees) = await DeleteEventAsync(db, ev.Id, userId, ct);
            if (res == SaveResult.Ok) { count++; foreach (var a in attendees) people.Add(a); }
        }
        return (SaveResult.Ok, anchor.Title, people.ToList(), count);
    }

    private static async Task<TimeZoneInfo> ZoneOfAsync(IPopDbContext db, Guid userId, CancellationToken ct) =>
        CalendarSharingEndpoints.ResolveTimeZone(await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZoneId).FirstOrDefaultAsync(ct));
}
