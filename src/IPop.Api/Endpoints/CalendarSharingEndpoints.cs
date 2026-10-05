using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Calendar sharing: everyone's free/busy is visible to every active employee; titles and details only to
/// people the owner shares with (<see cref="CalendarAccess"/>). Invited people always see the full meeting.
/// Also the per-account "Calendars I follow" list, settings, a person's calendar header and Find a time.
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

        g.MapPut("/settings", async (SaveSettingsRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var cur = await LoadSettingsAsync(db, me.Value, ct);
            await SaveSettingsAsync(db, me.Value, cur with
            {
                NotifyDelegateChanges = req.NotifyDelegateChanges ?? cur.NotifyDelegateChanges,
                NotifyShareRequests = req.NotifyShareRequests ?? cur.NotifyShareRequests,
                SharingIntroSeen = req.SharingIntroSeen == true || cur.SharingIntroSeen,
            }, ct);
            return Results.Ok(await LoadSettingsAsync(db, me.Value, ct));
        });

        // GET /api/calendar/preview?level=&from=&to= — my own calendar as someone at that access level sees it
        // (no level = everyone: busy times only).
        g.MapGet("/preview", async (string? level, DateTime? from, DateTime? to, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            if (!TryRange(from, to, out var f, out var t)) return BadRange();
            return Results.Ok(await BuildPreviewAsync(db, me.Value, DelegateLevel.IsValid(level) ? level : null, f, t, ct));
        });

        // GET /api/calendar/person/{nik} — header of someone's calendar page: who they are and what I may see.
        g.MapGet("/person/{nik}", async (string nik, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var p = await LoadPersonAsync(db, me.Value, nik, ct);
            return p is null ? Results.NotFound() : Results.Ok(p);
        });

        // POST /api/calendar/people/{nik}/ask — ask a colleague to share their calendar details (once a day per person).
        g.MapPost("/people/{nik}/ask", async (string nik, HttpContext http, IPopDbContext db, INotificationGatewayService? notifs, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            return await AskForDetailsAsync(db, notifs, MeetingEndpoints.NotifyLog(http), me.Value, nik, DateTime.UtcNow, ct) switch
            {
                AskResult.Sent => Results.Ok(new { sent = true }),
                AskResult.AlreadyAsked => Results.Ok(new { sent = false }),
                AskResult.Self => Results.Json(new { code = "self", message = "That's your own calendar." }, statusCode: 422),
                _ => Results.NotFound(),
            };
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

    /// DefaultVisibility no longer picks anything (events are Normal unless marked Private); the column now
    /// only remembers that the owner answered the one-time "now shows only busy times" card.
    private const string IntroSeen = "seen";

    public static async Task<SettingsDto> LoadSettingsAsync(IPopDbContext db, Guid userId, CancellationToken ct)
    {
        var s = await db.CalendarSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return new SettingsDto(s?.NotifyDelegateChanges ?? true, s?.NotifyShareRequests ?? true, s?.DefaultVisibility == IntroSeen);
    }

    public static async Task SaveSettingsAsync(IPopDbContext db, Guid userId, SettingsDto req, CancellationToken ct)
    {
        var s = await db.CalendarSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (s is null) { s = new CalendarSettings { UserId = userId }; db.CalendarSettings.Add(s); }
        s.NotifyDelegateChanges = req.NotifyDelegateChanges;
        s.NotifyShareRequests = req.NotifyShareRequests;
        if (req.SharingIntroSeen) s.DefaultVisibility = IntroSeen;
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// New events are Normal unless the form says Private.
    public static Task<string> DefaultEventVisibilityAsync(IPopDbContext db, Guid ownerId, CancellationToken ct) =>
        Task.FromResult(CalendarVisibility.Busy);

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

    /// Followed calendars with what I may see of each (Access null = busy times only).
    public static async Task<List<OtherCalendarDto>> ListOthersAsync(IPopDbContext db, Guid userId, CancellationToken ct)
    {
        var rows = await db.CalendarSubscriptions.AsNoTracking()
            .Where(s => s.UserId == userId && s.TargetUser.IsActive)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new
            {
                s.TargetUserId, s.TargetUser.Nik, s.TargetUser.Name,
                Department = s.TargetUser.Department != null ? s.TargetUser.Department.Name : null, s.Color, s.IsVisible,
            })
            .ToListAsync(ct);
        var levels = await CalendarAccess.LevelsAsync(db, userId, rows.Select(r => r.TargetUserId).ToList(), ct);
        return rows.Select(r => new OtherCalendarDto(r.Nik, r.Name, r.Department, r.Color, r.IsVisible, levels.GetValueOrDefault(r.TargetUserId))).ToList();
    }

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
        var levels = await CalendarAccess.LevelsAsync(db, viewerId, ids, ct);
        var blocks = await LoadBlocksAsync(db, ids, from, to, ct);
        // Meetings the viewer is invited to show in full whatever their visibility.
        var meetingIds = blocks.Where(b => b.MeetingId is not null).Select(b => b.MeetingId!.Value).Distinct().ToList();
        var invitedTo = (await db.MeetingAttendees.AsNoTracking()
            .Where(a => a.UserId == viewerId && meetingIds.Contains(a.MeetingId)).Select(a => a.MeetingId).ToListAsync(ct)).ToHashSet();
        var nikOf = owners.ToDictionary(o => o.Id, o => o.Nik);
        return blocks
            .Select(b =>
            {
                var invited = b.MeetingId is { } mid && invitedTo.Contains(mid);
                var (visibility, title, _) = CalendarAccess.Shown(b.Visibility, levels.GetValueOrDefault(b.OwnerId), invited, b.OwnerId == viewerId);
                return new PersonBusyItem(nikOf[b.OwnerId], b.Id, b.StartAt, b.EndAt, b.AllDay, b.Tentative, visibility,
                    title ? b.Title : null, title ? b.Location : null, invited ? b.MeetingId : null);
            })
            .OrderBy(b => b.StartAt).ThenBy(b => b.Nik).ToList();
    }

    /// My calendar as someone at <paramref name="level"/> sees it (null = everyone: busy times only).
    /// YourTitle is mine; TheySee is null when they only get a Busy block.
    public record PreviewItem(string Id, DateTime StartAt, DateTime EndAt, bool AllDay, bool Tentative, string Visibility,
        string YourTitle, string? TheySee);

    public static async Task<List<PreviewItem>> BuildPreviewAsync(IPopDbContext db, Guid ownerId, string? level,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var blocks = await LoadBlocksAsync(db, [ownerId], from, to, ct);
        return blocks
            .Select(b =>
            {
                var (visibility, title, _) = CalendarAccess.Shown(b.Visibility, level, invited: false, self: false);
                return new PreviewItem(b.Id, b.StartAt, b.EndAt, b.AllDay, b.Tentative, visibility, b.Title, title ? b.Title : null);
            })
            .OrderBy(i => i.StartAt).ToList();
    }

    // ── A person's calendar page ──

    /// Access = my level (null = busy times only); Following = in my Calendars list. NowBusyUntil / NextBusyAt
    /// feed the "Free now · busy 14:00" line on person cards.
    /// Id lets the page keep a reload-safe address without the NIK (/calendar/person/{id}).
    public record PersonDto(string Nik, string Name, string? Department, string? Site, string? Access, string AccessLabel,
        bool Following, bool Self, DateTime? NowBusyUntil, DateTime? NextBusyAt, Guid Id = default);

    /// <paramref name="key"/> is a NIK or a user id (links in notifications use the id, keeping NIKs out of URLs).
    public static async Task<PersonDto?> LoadPersonAsync(IPopDbContext db, Guid viewerId, string key, CancellationToken ct)
    {
        var id = Guid.TryParse(key, out var g) ? g : (Guid?)null;
        var u = await db.Users.AsNoTracking().Where(x => (id != null ? x.Id == id : x.Nik == key) && x.IsActive)
            .Select(x => new { x.Id, x.Nik, x.Name, Dept = x.Department != null ? x.Department.Name : null, Site = x.Site != null ? x.Site.Name : null })
            .FirstOrDefaultAsync(ct);
        if (u is null) return null;
        var self = u.Id == viewerId;
        var level = self ? null : await CalendarAccess.LevelAsync(db, viewerId, u.Id, ct);
        var following = await db.CalendarSubscriptions.AnyAsync(s => s.UserId == viewerId && s.TargetUserId == u.Id, ct);
        var now = DateTime.UtcNow;
        var timed = (await LoadBlocksAsync(db, [u.Id], now.AddHours(-12), now.AddHours(24), ct))
            .Where(b => !b.AllDay && b.EndAt > now).OrderBy(b => b.StartAt).ToList();
        var current = timed.Where(b => b.StartAt <= now).Select(b => (DateTime?)b.EndAt).DefaultIfEmpty().Max();
        var next = timed.Where(b => b.StartAt > now).Select(b => (DateTime?)b.StartAt).FirstOrDefault();
        return new PersonDto(u.Nik, u.Name, u.Dept, u.Site, level, CalendarAccess.ViewerLabel(level), following, self, current, next, u.Id);
    }

    public enum AskResult { Sent, AlreadyAsked, NotFound, Self }

    /// Sends the owner one notification ("X asks to see your calendar titles") that opens Allow — unless the
    /// owner turned these off. One request per person per day; the audit row doubles as the throttle.
    public static async Task<AskResult> AskForDetailsAsync(IPopDbContext db, INotificationGatewayService? notifs, ILogger? log,
        Guid askerId, string? ownerNik, DateTime now, CancellationToken ct)
    {
        var owner = await db.Users.AsNoTracking().Where(u => u.Nik == ownerNik && u.IsActive).Select(u => new { u.Id, u.Nik }).FirstOrDefaultAsync(ct);
        if (owner is null) return AskResult.NotFound;
        if (owner.Id == askerId) return AskResult.Self;
        var asker = await db.Users.AsNoTracking().Where(u => u.Id == askerId).Select(u => new { u.Id, u.Nik, u.Name }).FirstAsync(ct);
        var since = now.AddDays(-1);
        if (await db.AuditLogs.AnyAsync(a => a.Action == "calendar.share.ask" && a.ActorNik == asker.Nik && a.EntityId == owner.Nik && a.CreatedAt > since, ct))
            return AskResult.AlreadyAsked;
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = asker.Nik, Action = "calendar.share.ask", Entity = "CalendarSettings", EntityId = owner.Nik, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        var notify = await db.CalendarSettings.AsNoTracking().Where(s => s.UserId == owner.Id)
            .Select(s => (bool?)s.NotifyShareRequests).FirstOrDefaultAsync(ct) ?? true;
        if (notify)
            await MeetingEndpoints.PublishBestEffortAsync(notifs, log, "calendar.share.ask", $"{asker.Name} asks to see your calendar titles",
                "So they can plan meetings with you. Open it to allow — you can change or remove it any time in Calendar settings.",
                [owner.Id], "normal", $"/calendar?allow={asker.Id}");
        return AskResult.Sent;
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
        // Holidays block the whole local day for everyone.
        var holidays = await db.CompanyHolidays.AsNoTracking().Where(h => h.Date >= from.Date.AddDays(-1) && h.Date < to.Date.AddDays(1)).Select(h => h.Date).ToListAsync(ct);
        foreach (var d in holidays)
        {
            var dayStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d.Date, DateTimeKind.Unspecified), tz);
            busy.Add((dayStart, dayStart.AddDays(1)));
        }
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

    /// SharingIntroSeen: the one-time "your calendar now shows only busy times" card was answered.
    public record SettingsDto(bool NotifyDelegateChanges, bool NotifyShareRequests, bool SharingIntroSeen);
    public record SaveSettingsRequest(bool? NotifyDelegateChanges, bool? NotifyShareRequests, bool? SharingIntroSeen);
    public record AddOtherRequest(string? Nik);
    public record UpdateOtherRequest(string? Color, bool? IsVisible);
    /// Access = my level on their calendar, null = busy times only.
    public record OtherCalendarDto(string Nik, string Name, string? Department, string Color, bool IsVisible, string? Access = null);
}
