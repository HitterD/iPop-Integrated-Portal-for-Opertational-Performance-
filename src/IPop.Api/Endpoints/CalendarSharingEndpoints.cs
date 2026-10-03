using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Calendar sharing: everyone's free/busy is visible to every active employee; titles only for
/// Public events (unless the owner hides details). Invited people always see the full meeting.
/// Also the per-account "Other calendars" list, sharing settings and the Find a time assistant.
public static class CalendarSharingEndpoints
{
    public const int MaxOthers = 20;
    public static readonly string[] Palette = ["a", "b", "c", "d", "e", "f", "g", "h"];

    public static IEndpointRouteBuilder MapCalendarSharing(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/calendar").RequireAuthorization();

        g.MapGet("/settings", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            return Results.Ok(await LoadSettingsAsync(db, me.Value, ct));
        });

        g.MapPut("/settings", async (SettingsDto req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            if (!CalendarVisibility.IsDefault(req.DefaultVisibility))
                return Results.Json(new { code = "invalid_visibility", message = "Default must be public, busy or hidden." }, statusCode: 422);
            await SaveSettingsAsync(db, me.Value, req, ct);
            return Results.Ok(await LoadSettingsAsync(db, me.Value, ct));
        });

        g.MapGet("/others", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            return Results.Ok(await ListOthersAsync(db, me.Value, ct));
        });

        g.MapPost("/others", async (AddOtherRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var r = await AddOtherAsync(db, me.Value, req.Nik, ct);
            return r switch
            {
                AddOtherResult.NotFound => Results.Json(new { code = "not_found", message = "That person was not found." }, statusCode: 404),
                AddOtherResult.Self => Results.Json(new { code = "self", message = "Your own calendar is already shown." }, statusCode: 422),
                AddOtherResult.TooMany => Results.Json(new { code = "too_many", message = $"You can show up to {MaxOthers} other calendars." }, statusCode: 422),
                _ => Results.Ok(await ListOthersAsync(db, me.Value, ct)),
            };
        });

        g.MapPut("/others/{nik}", async (string nik, UpdateOtherRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var sub = await db.CalendarSubscriptions.FirstOrDefaultAsync(s => s.UserId == me && s.TargetUser.Nik == nik, ct);
            if (sub is null) return Results.NotFound();
            if (req.Color is { } c && Palette.Contains(c)) sub.Color = c;
            if (req.IsVisible is { } v) sub.IsVisible = v;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ListOthersAsync(db, me.Value, ct));
        });

        g.MapDelete("/others/{nik}", async (string nik, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var sub = await db.CalendarSubscriptions.FirstOrDefaultAsync(s => s.UserId == me && s.TargetUser.Nik == nik, ct);
            if (sub is not null) { db.CalendarSubscriptions.Remove(sub); await db.SaveChangesAsync(ct); }
            return Results.Ok(await ListOthersAsync(db, me.Value, ct));
        });

        // GET /api/calendar/people?niks=a,b&from=&to= — other people's busy blocks, details per visibility.
        g.MapGet("/people", async (string? niks, DateTime? from, DateTime? to, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            if (!TryRange(from, to, out var f, out var t)) return BadRange();
            var ids = await ResolveNiksAsync(db, niks, ct);
            return Results.Ok(await BuildPeopleAsync(db, me.Value, ids, f, t, ct));
        });

        // GET /api/calendar/freebusy?niks=a,b&from=&to=&minutes=60&meeting= — Find a time lanes + suggestions
        // (the meeting being edited is left out so it does not block its own slot).
        g.MapGet("/freebusy", async (string? niks, DateTime? from, DateTime? to, int? minutes, Guid? meeting, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            if (!TryRange(from, to, out var f, out var t) || (t - f).TotalDays > 31) return BadRange();
            var ids = await ResolveNiksAsync(db, niks, ct);
            if (!ids.Contains(me.Value)) ids.Insert(0, me.Value);
            var tzId = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.TimeZoneId).FirstOrDefaultAsync(ct);
            return Results.Ok(await BuildFreeBusyAsync(db, me.Value, ids, f, t, Math.Clamp(minutes ?? 60, 15, 480),
                ResolveTimeZone(tzId), DateTime.UtcNow, ct, meeting));
        });

        return app;
    }

    // ── Settings ──

    public static async Task<SettingsDto> LoadSettingsAsync(IPopDbContext db, Guid userId, CancellationToken ct)
    {
        var s = await db.CalendarSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return new SettingsDto(s?.DefaultVisibility ?? CalendarVisibility.Busy, s?.NotifyDelegateChanges ?? true);
    }

    public static async Task SaveSettingsAsync(IPopDbContext db, Guid userId, SettingsDto req, CancellationToken ct)
    {
        var s = await db.CalendarSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (s is null) { s = new CalendarSettings { UserId = userId }; db.CalendarSettings.Add(s); }
        s.DefaultVisibility = req.DefaultVisibility;
        s.NotifyDelegateChanges = req.NotifyDelegateChanges;
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// Visibility for a new event when the form sends none: the owner's default ("hidden" → busy).
    public static async Task<string> DefaultEventVisibilityAsync(IPopDbContext db, Guid ownerId, CancellationToken ct)
    {
        var d = await db.CalendarSettings.AsNoTracking().Where(x => x.UserId == ownerId).Select(x => x.DefaultVisibility).FirstOrDefaultAsync(ct);
        return d == CalendarVisibility.Public ? CalendarVisibility.Public : CalendarVisibility.Busy;
    }

    // ── Other calendars list ──

    public enum AddOtherResult { Added, NotFound, Self, TooMany }

    public static async Task<AddOtherResult> AddOtherAsync(IPopDbContext db, Guid userId, string? nik, CancellationToken ct)
    {
        var target = await db.Users.AsNoTracking().Where(u => u.Nik == nik && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        if (target is null) return AddOtherResult.NotFound;
        if (target == userId) return AddOtherResult.Self;
        var existing = await db.CalendarSubscriptions.Where(s => s.UserId == userId).ToListAsync(ct);
        if (existing.FirstOrDefault(s => s.TargetUserId == target) is { } sub)
        {
            sub.IsVisible = true;
            await db.SaveChangesAsync(ct);
            return AddOtherResult.Added;
        }
        if (existing.Count >= MaxOthers) return AddOtherResult.TooMany;
        // Next colour nobody in the list uses yet; wrap around once the palette is exhausted.
        var used = existing.Select(s => s.Color).ToHashSet();
        var color = Palette.FirstOrDefault(c => !used.Contains(c)) ?? Palette[existing.Count % Palette.Length];
        db.CalendarSubscriptions.Add(new CalendarSubscription
        {
            Id = Guid.NewGuid(), UserId = userId, TargetUserId = target.Value, Color = color, IsVisible = true, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return AddOtherResult.Added;
    }

    public static async Task<List<OtherCalendarDto>> ListOthersAsync(IPopDbContext db, Guid userId, CancellationToken ct) =>
        await db.CalendarSubscriptions.AsNoTracking()
            .Where(s => s.UserId == userId && s.TargetUser.IsActive)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new OtherCalendarDto(s.TargetUser.Nik, s.TargetUser.Name,
                s.TargetUser.Department != null ? s.TargetUser.Department.Name : null, s.Color, s.IsVisible))
            .ToListAsync(ct);

    // ── Other people's busy blocks ──

    /// One block on someone else's calendar. Title/Location are null unless the viewer may see them
    /// (Public and not hidden, or the viewer is invited). Tentative = an invite they haven't answered.
    public record PersonBusyItem(string Nik, string Id, DateTime StartAt, DateTime EndAt, bool AllDay, bool Tentative,
        string Visibility, string? Title, string? Location, Guid? MeetingId);

    internal sealed record Block(Guid OwnerId, string Id, DateTime StartAt, DateTime EndAt, bool AllDay, bool Tentative,
        string Visibility, string Title, string? Location, Guid? EventId, Guid? MeetingId);

    /// What counts as busy: personal events, plus meetings the person organizes or answered accept/maybe;
    /// unanswered invites are tentative. Declined/cancelled meetings, tasks, mail and news never show.
    internal static async Task<List<Block>> LoadBlocksAsync(IPopDbContext db, IReadOnlyCollection<Guid> ownerIds, DateTime from, DateTime to, CancellationToken ct)
    {
        var blocks = await db.CalendarEvents.AsNoTracking()
            .Where(e => ownerIds.Contains(e.OwnerId) && e.MeetingId == null && e.StartAt < to && e.EndAt > from)
            .Select(e => new Block(e.OwnerId, "event:" + e.Id, e.StartAt, e.EndAt, e.AllDay, false, e.Visibility, e.Title, e.Location, e.Id, null))
            .ToListAsync(ct);
        var meetings = await db.MeetingAttendees.AsNoTracking()
            .Where(a => ownerIds.Contains(a.UserId) && a.RsvpStatus != "decline"
                && a.Meeting.Status != MeetingStatus.Cancelled && a.Meeting.StartAt < to && a.Meeting.EndAt > from)
            .Select(a => new
            {
                a.UserId, a.MeetingId, a.RsvpStatus, a.Meeting.OrganizerId, a.Meeting.StartAt, a.Meeting.EndAt,
                a.Meeting.Visibility, a.Meeting.Title, a.Meeting.Location,
                AllDay = db.CalendarEvents.Where(e => e.MeetingId == a.MeetingId).Select(e => e.AllDay).FirstOrDefault(),
            })
            .ToListAsync(ct);
        blocks.AddRange(meetings.Select(m => new Block(m.UserId, $"meeting:{m.MeetingId}", m.StartAt, m.EndAt, m.AllDay,
            m.RsvpStatus == "pending" && m.OrganizerId != m.UserId, m.Visibility, m.Title, m.Location, null, m.MeetingId)));
        return blocks;
    }

    public static async Task<List<PersonBusyItem>> BuildPeopleAsync(IPopDbContext db, Guid viewerId, IReadOnlyList<Guid> ownerIds,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id) && u.IsActive).Select(u => new { u.Id, u.Nik }).ToListAsync(ct);
        if (owners.Count == 0) return [];
        var ids = owners.Select(o => o.Id).ToList();
        var hiding = (await db.CalendarSettings.AsNoTracking()
            .Where(s => ids.Contains(s.UserId) && s.DefaultVisibility == CalendarVisibility.Hidden).Select(s => s.UserId).ToListAsync(ct)).ToHashSet();
        var blocks = await LoadBlocksAsync(db, ids, from, to, ct);
        // Meetings the viewer is invited to show in full whatever their visibility.
        var meetingIds = blocks.Where(b => b.MeetingId is not null).Select(b => b.MeetingId!.Value).Distinct().ToList();
        var invitedTo = (await db.MeetingAttendees.AsNoTracking()
            .Where(a => a.UserId == viewerId && meetingIds.Contains(a.MeetingId)).Select(a => a.MeetingId).ToListAsync(ct)).ToHashSet();
        // Delegates see the details of everything but Private, even when the owner hides details.
        var managed = (await db.CalendarDelegates.AsNoTracking()
            .Where(d => d.DelegateId == viewerId && ids.Contains(d.OwnerId)).Select(d => d.OwnerId).ToListAsync(ct)).ToHashSet();
        var nikOf = owners.ToDictionary(o => o.Id, o => o.Nik);
        return blocks
            .Select(b =>
            {
                var invited = b.MeetingId is { } mid && invitedTo.Contains(mid);
                var visibility = hiding.Contains(b.OwnerId) && b.Visibility == CalendarVisibility.Public ? CalendarVisibility.Busy : b.Visibility;
                var details = b.OwnerId == viewerId || invited || visibility == CalendarVisibility.Public
                    || (managed.Contains(b.OwnerId) && b.Visibility != CalendarVisibility.Private);
                return new PersonBusyItem(nikOf[b.OwnerId], b.Id, b.StartAt, b.EndAt, b.AllDay, b.Tentative, visibility,
                    details ? b.Title : null, details ? b.Location : null, invited ? b.MeetingId : null);
            })
            .OrderBy(b => b.StartAt).ThenBy(b => b.Nik).ToList();
    }

    // ── Find a time ──

    public record FreeBusyBlock(DateTime StartAt, DateTime EndAt, bool Tentative);
    public record FreeBusyLane(string Nik, string Name, IReadOnlyList<FreeBusyBlock> Blocks);
    public record FreeBusyDto(IReadOnlyList<FreeBusyLane> Lanes, IReadOnlyList<DateTime> Suggestions);

    public static async Task<FreeBusyDto> BuildFreeBusyAsync(IPopDbContext db, Guid viewerId, IReadOnlyList<Guid> userIds,
        DateTime from, DateTime to, int minutes, TimeZoneInfo tz, DateTime nowUtc, CancellationToken ct, Guid? ignoreMeetingId = null)
    {
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id) && u.IsActive).Select(u => new { u.Id, u.Nik, u.Name }).ToListAsync(ct);
        var blocks = (await LoadBlocksAsync(db, users.Select(u => u.Id).ToList(), from, to, ct))
            .Where(b => ignoreMeetingId is null || b.MeetingId != ignoreMeetingId).ToList();
        var lanes = users.OrderBy(u => u.Id == viewerId ? 0 : 1).ThenBy(u => u.Name)
            .Select(u => new FreeBusyLane(u.Nik, u.Name, blocks.Where(b => b.OwnerId == u.Id && !b.AllDay)
                .Select(b => new FreeBusyBlock(b.StartAt, b.EndAt, b.Tentative)).OrderBy(b => b.StartAt).ToList()))
            .ToList();
        var busy = blocks.Where(b => !b.AllDay).Select(b => (b.StartAt, b.EndAt)).ToList();
        return new FreeBusyDto(lanes, SuggestSlots(busy, from, to, minutes, tz, nowUtc));
    }

    /// Start times free for everyone (tentative counts as busy): weekdays, 08:00–17:00 in the viewer's
    /// time zone, on the half hour, spread an hour apart, at most <paramref name="max"/>.
    public static List<DateTime> SuggestSlots(IReadOnlyList<(DateTime Start, DateTime End)> busy, DateTime fromUtc, DateTime toUtc,
        int minutes, TimeZoneInfo tz, DateTime nowUtc, int max = 8)
    {
        var result = new List<DateTime>();
        var len = TimeSpan.FromMinutes(minutes);
        var lastDay = TimeZoneInfo.ConvertTimeFromUtc(toUtc, tz).Date;
        for (var day = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz).Date; day <= lastDay && result.Count < max; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var dayEnd = day.AddHours(17);
            for (var s = day.AddHours(8); s + len <= dayEnd && result.Count < max; s = s.AddMinutes(30))
            {
                var su = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(s, DateTimeKind.Unspecified), tz);
                var eu = su + len;
                if (su < fromUtc || eu > toUtc || su < nowUtc) continue;
                if (busy.Any(b => b.Start < eu && b.End > su)) continue;
                result.Add(DateTime.SpecifyKind(su, DateTimeKind.Utc));
                s = s.AddMinutes(30);
            }
        }
        return result;
    }

    // ── Helpers ──

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        foreach (var candidate in new[] { id, "SE Asia Standard Time", "Asia/Jakarta" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); } catch { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    }

    private static async Task<List<Guid>> ResolveNiksAsync(IPopDbContext db, string? niks, CancellationToken ct)
    {
        var list = (niks ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().Take(MaxOthers + 30).ToList();
        if (list.Count == 0) return [];
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Nik) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
    }

    private static bool TryRange(DateTime? from, DateTime? to, out DateTime f, out DateTime t)
    {
        f = AsUtc(from ?? DateTime.UtcNow.Date);
        t = AsUtc(to ?? f.AddDays(7));
        return t > f && (t - f).TotalDays <= 100;
    }

    private static IResult BadRange() =>
        Results.Json(new { code = "invalid_range", message = "Pick a shorter date range." }, statusCode: 422);

    private static DateTime AsUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    public record SettingsDto(string DefaultVisibility, bool NotifyDelegateChanges);
    public record AddOtherRequest(string? Nik);
    public record UpdateOtherRequest(string? Color, bool? IsVisible);
    public record OtherCalendarDto(string Nik, string Name, string? Department, string Color, bool IsVisible);
}
