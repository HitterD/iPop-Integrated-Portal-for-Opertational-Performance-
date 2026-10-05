using IPop.Application.Notifications;
using IPop.Application.Rbac;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Kalender terpadu: satu feed dari semua modul (event pribadi, rapat, event ruang chat,
/// tugas & milestone & hari libur proyek, email terjadwal, berita/memo) plus CRUD event.
/// Setiap sumber dibatasi hak akses pemanggil — tidak ada data milik orang lain yang bocor.
public static partial class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendar(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/calendar").RequireAuthorization();

        // GET /api/calendar/feed?from=&to= — item dari semua sumber yang boleh dilihat.
        g.MapGet("/feed", async (DateTime? from, DateTime? to, HttpContext http, IPopDbContext db, IPermissionEvaluator perms, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var f = AsUtc(from ?? DateTime.UtcNow.Date.AddDays(-7));
            var t = AsUtc(to ?? DateTime.UtcNow.Date.AddDays(42));
            if (t <= f || (t - f).TotalDays > 400) return Results.Json(new { code = "invalid_range", message = "Range must be 1–400 days." }, statusCode: 422);
            var access = await FeedAccess.ForAsync(perms, userId.Value, ct);
            return Results.Ok(await BuildFeedAsync(db, userId.Value, f, t, access, ct));
        });

        // GET /api/calendar/events/{id} — detail event (pemilik atau peserta rapatnya).
        g.MapGet("/events/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var dto = await LoadDetailAsync(db, id, userId.Value, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        // GET /api/calendar/meetings/{meetingId}/event — the calendar row that opens this meeting for me
        // (my own row, else the organizer's), so Calls, chat cards and reminders can open it on the calendar.
        g.MapGet("/meetings/{meetingId:guid}/event", async (Guid meetingId, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var id = await db.CalendarEvents.AsNoTracking().Where(e => e.MeetingId == meetingId)
                .OrderBy(e => e.OwnerId == userId ? 0 : 1).ThenBy(e => e.CreatedAt).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
            if (id is null || await LoadDetailAsync(db, id.Value, userId.Value, ct) is not { } d) return Results.NotFound();
            return Results.Ok(new { eventId = d.Id, startAt = d.StartAt });
        });

        g.MapPost("/events", async (SaveEventRequest req, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            // ownerNik = a delegate creating on someone else's calendar (they become the owner/organizer).
            var (ownerId, actedBy, denied) = await CalendarDelegateEndpoints.ResolveOwnerAsync(db, userId.Value, req.OwnerNik, DelegateLevel.Edit, ct);
            if (denied is not null) return denied;
            var (result, ev, invited) = await CreateSeriesAsync(db, ownerId, req, ct, actedBy);
            if (result != SaveResult.Ok) return Error(result);
            var log = MeetingEndpoints.NotifyLog(http);
            await NotifyChangedAsync(db, realtime, ev!.MeetingId, [ownerId, userId.Value], ct);
            await NotifyInvitedAsync(db, notifs, ev!, ownerId, invited, log, ct, actedBy);
            if (actedBy is { } by) await CalendarDelegateEndpoints.NotifyOwnerOfChangeAsync(db, notifs, log, ownerId, by, "added", ev!.Title, ct);
            return Results.Ok(await LoadDetailAsync(db, ev!.Id, userId.Value, ct));
        });

        // PUT /api/calendar/events/{id}?scope=this|following|all — "following"/"all" apply to a repeating series.
        g.MapPut("/events/{id:guid}", async (Guid id, string? scope, SaveEventRequest req, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (scope is "following" or "all")
            {
                var (sOwner, sActed) = await CalendarDelegateEndpoints.ActorForEventAsync(db, id, userId.Value, ct);
                var (sr, firstId, touched) = await UpdateSeriesAsync(db, id, sOwner, req, scope, ct, sActed);
                if (sr != SaveResult.Ok) return Error(sr);
                await MeetingEndpoints.NotifyCalendarChangedAsync(db, realtime, touched.Append(sOwner).Append(userId.Value), ct);
                return Results.Ok(await LoadDetailAsync(db, firstId, userId.Value, ct));
            }
            // Attendees an edit removes must also drop the event from their calendar.
            var beforeMeeting = await db.CalendarEvents.AsNoTracking().Where(e => e.Id == id).Select(e => e.MeetingId).FirstOrDefaultAsync(ct);
            var before = beforeMeeting is { } bm
                ? await db.MeetingAttendees.AsNoTracking().Where(a => a.MeetingId == bm).Select(a => a.UserId).ToListAsync(ct)
                : [];
            var (ownerId, actedBy) = await CalendarDelegateEndpoints.ActorForEventAsync(db, id, userId.Value, ct);
            var (result, ev, invited) = await UpdateEventAsync(db, id, ownerId, req, ct, actedBy);
            if (result != SaveResult.Ok) return Error(result);
            var log = MeetingEndpoints.NotifyLog(http);
            await NotifyChangedAsync(db, realtime, ev!.MeetingId, before.Append(ownerId).Append(userId.Value), ct);
            await NotifyInvitedAsync(db, notifs, ev!, ownerId, invited, log, ct, actedBy);
            if (actedBy is { } by) await CalendarDelegateEndpoints.NotifyOwnerOfChangeAsync(db, notifs, log, ownerId, by, "changed", ev!.Title, ct);
            return Results.Ok(await LoadDetailAsync(db, id, userId.Value, ct));
        });

        g.MapDelete("/events/{id:guid}", async (Guid id, string? scope, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (scope is "following" or "all")
            {
                var (sOwner, _) = await CalendarDelegateEndpoints.ActorForEventAsync(db, id, userId.Value, ct);
                var (sr, sTitle, sPeople, count) = await DeleteSeriesAsync(db, id, sOwner, scope, ct);
                if (sr != SaveResult.Ok) return Error(sr);
                await MeetingEndpoints.NotifyCalendarChangedAsync(db, realtime, sPeople.Append(sOwner).Append(userId.Value), ct);
                await MeetingEndpoints.PublishBestEffortAsync(notifs, MeetingEndpoints.NotifyLog(http), "meeting.cancelled",
                    $"Meetings cancelled: {sTitle}", $"{count} upcoming \"{sTitle}\" meetings have been cancelled.",
                    sPeople.Where(a => a != userId.Value).ToList(), "high", "/calendar");
                return Results.NoContent();
            }
            var meetingId = await db.CalendarEvents.AsNoTracking().Where(e => e.Id == id).Select(e => e.MeetingId).FirstOrDefaultAsync(ct);
            var (ownerId, actedBy) = await CalendarDelegateEndpoints.ActorForEventAsync(db, id, userId.Value, ct);
            var (result, title, attendees) = await DeleteEventAsync(db, id, ownerId, ct);
            if (result != SaveResult.Ok) return Error(result);
            var log = MeetingEndpoints.NotifyLog(http);
            await NotifyChangedAsync(db, realtime, meetingId, attendees.Append(ownerId).Append(userId.Value), ct);
            await MeetingEndpoints.PublishBestEffortAsync(notifs, log, "meeting.cancelled",
                $"Meeting cancelled: {title}", $"\"{title}\" has been cancelled.", attendees.Where(a => a != userId.Value).ToList(), "high", "/calendar");
            if (actedBy is { } by) await CalendarDelegateEndpoints.NotifyOwnerOfChangeAsync(db, notifs, log, ownerId, by, meetingId is null ? "deleted" : "cancelled", title, ct);
            return Results.NoContent();
        });

        return app;
    }

    /// Live refresh after a calendar write. A meeting-backed event fans out meetingChanged +
    /// calendarChanged to the organizer and attendees; a personal event only to its owner.
    /// Events are private (owner + invitees): there is no company-wide visibility to broadcast.
    public static Task NotifyChangedAsync(IPopDbContext db, IRealtime realtime, Guid? meetingId, IEnumerable<Guid> alsoNotify, CancellationToken ct)
        => meetingId is { } mid
            ? MeetingEndpoints.NotifyMeetingChangedAsync(db, realtime, mid, alsoNotify, ct)
            : MeetingEndpoints.NotifyCalendarChangedAsync(db, realtime, alsoNotify, ct);

    // ── Feed ──────────────────────────────────────────────────────────────────

    /// Which module sources the caller may read (mirrors each module's own read permission).
    public sealed record FeedAccess(bool Projects, bool Mail, bool Chat, bool News)
    {
        public static readonly FeedAccess All = new(true, true, true, true);

        public static async Task<FeedAccess> ForAsync(IPermissionEvaluator perms, Guid userId, CancellationToken ct) => new(
            await perms.IsAllowedAsync(userId, "projects.read", null, ct),
            await perms.IsAllowedAsync(userId, "mail.read", null, ct),
            await perms.IsAllowedAsync(userId, "chat.read", null, ct),
            await perms.IsAllowedAsync(userId, "news.read", null, ct));
    }

    public static async Task<List<CalendarFeedItem>> BuildFeedAsync(IPopDbContext db, Guid userId, DateTime from, DateTime to, FeedAccess access, CancellationToken ct)
    {
        var items = new List<CalendarFeedItem>();
        // Tanggal all-day disimpan sebagai tengah malam UTC; beri kelonggaran sehari agar zona waktu lokal tidak memotongnya.
        var dayFrom = from.Date.AddDays(-1);
        var dayTo = to.Date.AddDays(1);

        // 1) Event pribadi (tanpa rapat).
        var personal = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.OwnerId == userId && e.MeetingId == null && e.StartAt < to && e.EndAt > from)
            .ToListAsync(ct);
        items.AddRange(personal.Select(e => new CalendarFeedItem(
            $"event:{e.Id}", "personal", e.Type == "rapat" ? "meeting" : "event", e.Title, e.StartAt, e.EndAt, e.AllDay,
            null, null, e.Id, null, null, true, e.Location, e.Link, e.Visibility)));

        // 2) Rapat yang saya ikuti/selenggarakan (termasuk event ruang chat), kecuali dibatalkan.
        var meetings = await db.Meetings.AsNoTracking()
            .Where(m => m.Status != MeetingStatus.Cancelled && m.StartAt < to && m.EndAt > from
                && (m.OrganizerId == userId || m.Attendees.Any(a => a.UserId == userId && a.RsvpStatus != "decline")))
            .Select(m => new
            {
                m.Id, m.Title, m.StartAt, m.EndAt, m.Location, m.Link, m.RoomId, m.OrganizerId, m.Visibility,
                OrganizerName = m.Organizer.Name,
                Rsvp = m.Attendees.Where(a => a.UserId == userId).Select(a => a.RsvpStatus).FirstOrDefault(),
                EventId = db.CalendarEvents.Where(e => e.MeetingId == m.Id).OrderBy(e => e.CreatedAt).Select(e => (Guid?)e.Id).FirstOrDefault(),
                AllDay = db.CalendarEvents.Where(e => e.MeetingId == m.Id).Select(e => e.AllDay).FirstOrDefault(),
                RoomName = db.ChatRooms.Where(r => r.Id == m.RoomId).Select(r => r.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        foreach (var m in meetings)
        {
            var isRoom = m.RoomId is not null;
            if (isRoom && !access.Chat) continue;
            items.Add(new CalendarFeedItem(
                $"meeting:{m.Id}", isRoom ? "chat" : "meetings", "meeting", m.Title, m.StartAt, m.EndAt, m.AllDay,
                m.EventId is null ? (isRoom ? $"/chat/{m.RoomId}" : $"/calls?tab=scheduled&meeting={m.Id}") : null,
                isRoom ? m.RoomName : $"Organizer: {m.OrganizerName}",
                m.EventId, m.Id, m.Rsvp ?? (m.OrganizerId == userId ? "accept" : null), m.OrganizerId == userId, m.Location, m.Link, m.Visibility));
        }

        // 3) Proyek saya: tugas saya yang belum selesai, milestone, hari libur/off proyek.
        if (access.Projects)
        {
            var myProjects = db.ProjectMembers.Where(pm => pm.UserId == userId).Select(pm => pm.ProjectId)
                .Union(db.Projects.Where(p => p.OwnerId == userId).Select(p => p.Id));

            var tasks = await db.ProjectTasks.AsNoTracking()
                .Where(x => x.DueDate != null && x.DueDate >= dayFrom && x.DueDate < dayTo
                    && x.Status != "done" && !x.IsTemplate && x.Project.IsActive
                    && x.Assignees.Any(a => a.UserId == userId))
                .Select(x => new { x.Id, x.Title, x.DueDate, x.ProjectId, ProjectName = x.Project.Name, x.Priority, x.Status })
                .ToListAsync(ct);
            var today = DateTime.UtcNow.Date;
            // A due date with a time of day shows at that time (30 min); a plain date stays all-day.
            items.AddRange(tasks.Select(x => new CalendarFeedItem(
                $"task:{x.Id}", "projects", x.DueDate!.Value.Date < today ? "task-overdue" : "task", x.Title,
                x.DueDate!.Value.TimeOfDay == TimeSpan.Zero ? x.DueDate!.Value.Date : x.DueDate!.Value.AddMinutes(-30),
                x.DueDate!.Value.TimeOfDay == TimeSpan.Zero ? x.DueDate!.Value.Date.AddDays(1) : x.DueDate!.Value,
                x.DueDate!.Value.TimeOfDay == TimeSpan.Zero,
                $"/projects/{x.ProjectId}?task={x.Id}", $"{x.ProjectName} · due", null, null, null, false, null, null)));

            var milestones = await db.ProjectMilestones.AsNoTracking()
                .Where(ms => ms.DueDate >= dayFrom && ms.DueDate < dayTo && ms.Project.IsActive && myProjects.Contains(ms.ProjectId))
                .Select(ms => new { ms.Id, ms.Title, ms.DueDate, ms.AchievedAt, ms.ProjectId, ProjectName = ms.Project.Name })
                .ToListAsync(ct);
            items.AddRange(milestones.Select(ms => new CalendarFeedItem(
                $"milestone:{ms.Id}", "projects", ms.AchievedAt is null ? "milestone" : "milestone-done", ms.Title,
                ms.DueDate.Date, ms.DueDate.Date.AddDays(1), true,
                $"/projects/{ms.ProjectId}", $"{ms.ProjectName} · milestone", null, null, null, false, null, null)));

            var offDays = await db.WorkCalendarEntries.AsNoTracking()
                .Where(w => w.Kind != "workday" && w.Date >= dayFrom && w.Date < dayTo && w.Project.IsActive && myProjects.Contains(w.ProjectId))
                .Select(w => new { w.Id, w.Date, w.Kind, w.Title, w.ProjectId, ProjectName = w.Project.Name })
                .ToListAsync(ct);
            items.AddRange(offDays.Select(w => new CalendarFeedItem(
                $"workday:{w.Id}", "projects", w.Kind, w.Title ?? (w.Kind == "holiday" ? "Holiday" : "Team off day"),
                w.Date.Date, w.Date.Date.AddDays(1), true,
                $"/projects/{w.ProjectId}", w.ProjectName, null, null, null, false, null, null)));
        }

        // 4) Email terjadwal saya.
        if (access.Mail)
        {
            var mails = await db.Mails.AsNoTracking()
                .Where(m => m.SenderId == userId && m.Status == "scheduled" && m.CancelledAt == null
                    && m.ScheduledAt != null && m.ScheduledAt >= from && m.ScheduledAt < to)
                .Select(m => new { m.Id, m.Subject, m.ScheduledAt })
                .ToListAsync(ct);
            items.AddRange(mails.Select(m => new CalendarFeedItem(
                $"mail:{m.Id}", "mail", "scheduled-mail", string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject,
                m.ScheduledAt!.Value, m.ScheduledAt!.Value.AddMinutes(15), false,
                $"/mail?mailId={m.Id}", "Scheduled send", null, null, null, false, null, null)));
        }

        // 5) Berita/memo: terbit dalam rentang (sesuai target pembaca), batas konfirmasi baca saya,
        //    dan — bagi pengelola berita — jadwal terbit yang akan datang.
        if (access.News)
        {
            var visible = await VisibleNewsAsync(db, userId, ct);
            var canManage = await db.UserRoles.AsNoTracking().AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct)
                || await (from ur in db.UserRoles join rp in db.RolePermissions on ur.RoleId equals rp.RoleId
                          where ur.UserId == userId && rp.Permission.Code == "news.publish" select rp).AnyAsync(ct);
            var now = DateTime.UtcNow;
            var posts = await visible
                .Where(p => p.Status == "published")
                .Where(p => (p.PublishAt ?? p.PublishedAt) >= from && (p.PublishAt ?? p.PublishedAt) < to)
                .Where(p => canManage || p.PublishAt == null || p.PublishAt <= now)
                .Select(p => new { p.Id, p.Title, p.Type, At = (p.PublishAt ?? p.PublishedAt)!.Value })
                .ToListAsync(ct);
            items.AddRange(posts.Select(p => new CalendarFeedItem(
                $"news:{p.Id}", "news", p.At > now ? "news-scheduled" : p.Type, p.Title, p.At, p.At.AddMinutes(30), false,
                $"/news/{p.Id}", p.At > now ? $"Scheduled {TypeLabel(p.Type)}" : TypeLabel(p.Type), null, null, null, false, null, null)));

            var acked = db.NewsAcknowledgements.Where(a => a.UserId == userId).Select(a => a.NewsPostId);
            var ackDue = await visible
                .Where(p => p.Status == "published" && p.RequiresAcknowledgement && p.AckDueAt != null
                    && p.AckDueAt >= from && p.AckDueAt < to && (p.PublishAt == null || p.PublishAt <= now) && !acked.Contains(p.Id))
                .Select(p => new { p.Id, p.Title, p.AckDueAt })
                .ToListAsync(ct);
            items.AddRange(ackDue.Select(p => new CalendarFeedItem(
                $"ack:{p.Id}", "news", "ack-due", $"Confirm read: {p.Title}", p.AckDueAt!.Value, p.AckDueAt!.Value.AddMinutes(15), false,
                $"/news/{p.Id}", "Acknowledgement due", null, null, null, false, null, null)));
        }

        // 6) National and company holidays — everyone sees them.
        var holidays = await db.CompanyHolidays.AsNoTracking()
            .Where(h => h.Date >= dayFrom && h.Date < dayTo).ToListAsync(ct);
        items.AddRange(holidays.Select(h => new CalendarFeedItem(
            $"holiday:{h.Id}", "holidays", h.Kind == "company" ? "company-holiday" : "holiday", h.Title,
            h.Date.Date, h.Date.Date.AddDays(1), true, null, h.Kind == "company" ? "Company holiday" : "National holiday",
            null, null, null, false, null, null)));

        // 7) My open iDesk tickets with a target date (SLA) or a scheduled visit.
        var tickets = await db.Tickets.AsNoTracking()
            .Where(x => x.UserId == userId && x.RemovedAt == null && x.Status != "CLOSED" && x.Status != "CANCELLED" && x.Status != "RESOLVED"
                && x.SlaTarget != null && x.SlaTarget >= from && x.SlaTarget < to)
            .Select(x => new { x.Id, x.Title, x.TicketNumber, x.SlaTarget })
            .ToListAsync(ct);
        items.AddRange(tickets.Select(x => new CalendarFeedItem(
            $"ticket:{x.Id}", "tickets", "ticket-due", x.Title, x.SlaTarget!.Value.AddMinutes(-30), x.SlaTarget!.Value, false,
            $"/tickets/{x.Id}", x.TicketNumber is null ? "Ticket due" : $"Ticket {x.TicketNumber} due", null, null, null, false, null, null)));

        return items.OrderBy(i => i.StartAt).ThenBy(i => i.Title).ToList();
    }

    private static string TypeLabel(string type) => type switch
    {
        "memo" => "Memo",
        "announcement" => "Announcement",
        "condolence" or "duka" => "Condolence",
        _ => "News",
    };

    /// Same targeting rules as the News reader feed: untargeted = everyone; otherwise all / user NIK /
    /// department code / role name / project membership.
    private static async Task<IQueryable<NewsPost>> VisibleNewsAsync(IPopDbContext db, Guid userId, CancellationToken ct)
    {
        var viewer = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var roles = await db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId).Select(ur => ur.Role.Name).ToListAsync(ct);
        var deptCode = viewer.DepartmentId is null ? null
            : await db.Departments.AsNoTracking().Where(d => d.Id == viewer.DepartmentId).Select(d => d.Code).FirstOrDefaultAsync(ct);
        var projects = (await db.ProjectMembers.AsNoTracking().Where(pm => pm.UserId == userId).Select(pm => pm.ProjectId).ToListAsync(ct))
            .Select(id => id.ToString("D")).ToList();
        var nik = viewer.Nik;
        return db.NewsPosts.AsNoTracking().Where(p =>
            !db.NewsTargets.Any(t => t.NewsPostId == p.Id) ||
            db.NewsTargets.Any(t => t.NewsPostId == p.Id && (
                t.Kind == "all" ||
                (t.Kind == "user" && t.Value == nik) ||
                (t.Kind == "department" && deptCode != null && t.Value == deptCode) ||
                (t.Kind == "role" && roles.Contains(t.Value!)) ||
                (t.Kind == "project" && projects.Contains(t.Value!)))));
    }

    // ── Event CRUD ────────────────────────────────────────────────────────────

    public enum SaveResult { Ok, NotFound, Forbidden, InvalidTitle, InvalidRange, NotRoomMember, Cancelled }

    private static IResult Error(SaveResult r) => r switch
    {
        SaveResult.NotFound => Results.NotFound(),
        SaveResult.Forbidden => Results.Json(new { code = "forbidden", message = "Only the organizer or their delegate can change this event." }, statusCode: 403),
        SaveResult.NotRoomMember => Results.Json(new { code = "forbidden", message = "You are not a member of that group." }, statusCode: 403),
        SaveResult.InvalidTitle => Results.Json(new { code = "invalid_title", message = "Title is required (max 200 characters)." }, statusCode: 422),
        SaveResult.InvalidRange => Results.Json(new { code = "invalid_range", message = "End must be after start." }, statusCode: 422),
        SaveResult.Cancelled => Results.Json(new { code = "cancelled", message = "This meeting was cancelled." }, statusCode: 409),
        _ => Results.StatusCode(500),
    };

    private static (SaveResult, string Title, DateTime Start, DateTime End) Normalize(SaveEventRequest req)
    {
        var title = req.Title?.Trim() ?? "";
        if (title.Length is 0 or > 200) return (SaveResult.InvalidTitle, "", default, default);
        var start = AsUtc(req.StartAt);
        var end = AsUtc(req.EndAt);
        if (req.AllDay)
        {
            // All-day: tanggal saja, akhir eksklusif (minimal satu hari).
            start = DateTime.SpecifyKind(req.StartAt.Date, DateTimeKind.Utc);
            end = DateTime.SpecifyKind(req.EndAt.Date, DateTimeKind.Utc);
            if (end <= start) end = start.AddDays(1);
        }
        if (end <= start) return (SaveResult.InvalidRange, "", default, default);
        return (SaveResult.Ok, title, start, end);
    }

    private static string? Clean(string? s, int max)
    {
        var v = s?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
    }

    private static void ApplyDetails(CalendarEvent ev, SaveEventRequest req, string title, DateTime start, DateTime end)
    {
        var reminder = req.ReminderMinutes is > 0 and <= 10080 ? req.ReminderMinutes : null;
        if (ev.StartAt != start || ev.ReminderMinutes != reminder) ev.ReminderSentAt = null;
        ev.Title = title; ev.StartAt = start; ev.EndAt = end; ev.AllDay = req.AllDay;
        ev.Location = Clean(req.Location, 300);
        ev.Link = Clean(req.Link, 1000);
        ev.Notes = Clean(req.Notes, 4000);
        ev.ReminderMinutes = reminder;
        if (CalendarVisibility.IsEventLevel(req.Visibility)) ev.Visibility = req.Visibility!;
        if (!string.IsNullOrWhiteSpace(req.Type)) ev.Type = req.Type.Trim().ToLowerInvariant() is "rapat" or "kerja" or "pribadi" ? req.Type.Trim().ToLowerInvariant() : ev.Type;
    }

    /// Creates a personal event, or — with attendees or a chat group — a Meeting (RSVP + reminders)
    /// linked to the organizer's calendar row. Returns the users newly invited.
    /// <paramref name="actedBy"/> = a delegate creating it on <paramref name="userId"/>'s behalf.
    public static async Task<(SaveResult, CalendarEvent?, IReadOnlyList<Guid>)> CreateEventAsync(IPopDbContext db, Guid userId, SaveEventRequest req, CancellationToken ct,
        Guid? actedBy = null)
    {
        var (r, title, start, end) = Normalize(req);
        if (r != SaveResult.Ok) return (r, null, []);
        var ev = new CalendarEvent
        {
            Id = Guid.NewGuid(), Title = title, OwnerId = userId, RoomId = req.RoomId, CreatedAt = DateTime.UtcNow, Type = "kerja",
            Visibility = await CalendarSharingEndpoints.DefaultEventVisibilityAsync(db, userId, ct), CreatedById = actedBy,
        };
        ApplyDetails(ev, req, title, start, end);

        IReadOnlyList<Guid> invited = [];
        if (req.RoomId is not null || req.HasAttendees)
        {
            var (ok, ids) = await ResolveAttendeesAsync(db, userId, req, ct);
            if (!ok) return (SaveResult.NotRoomMember, null, []);
            var meeting = new Meeting
            {
                Id = Guid.NewGuid(), Title = title, StartAt = start, EndAt = end, OrganizerId = userId, Status = "aktif",
                RoomId = req.RoomId, Location = ev.Location, Link = ev.Link, Agenda = ev.Notes,
                ReminderMinutes = ev.ReminderMinutes, Visibility = ev.Visibility, CreatedAt = DateTime.UtcNow, SentById = actedBy,
            };
            db.Meetings.Add(meeting);
            foreach (var uid in ids)
                db.MeetingAttendees.Add(new MeetingAttendee
                {
                    Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = uid, Required = true,
                    RsvpStatus = uid == userId ? "accept" : "pending", RespondedAt = uid == userId ? DateTime.UtcNow : null,
                });
            AddReminders(db, meeting.Id, start, ev.ReminderMinutes, ids);
            ev.MeetingId = meeting.Id;
            ev.Type = "rapat";
            invited = ids.Where(id => id != userId).ToList();
        }
        db.CalendarEvents.Add(ev);
        await db.SaveChangesAsync(ct);
        return (SaveResult.Ok, ev, invited);
    }

    /// <paramref name="actedBy"/> = a delegate editing on <paramref name="userId"/>'s behalf.
    public static async Task<(SaveResult, CalendarEvent?, IReadOnlyList<Guid>)> UpdateEventAsync(IPopDbContext db, Guid id, Guid userId, SaveEventRequest req, CancellationToken ct,
        Guid? actedBy = null)
    {
        var ev = await db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev is null) return (SaveResult.NotFound, null, []);
        if (ev.OwnerId != userId) return (SaveResult.Forbidden, null, []);
        var (r, title, start, end) = Normalize(req);
        if (r != SaveResult.Ok) return (r, null, []);

        IReadOnlyList<Guid> invited = [];
        if (ev.MeetingId is { } meetingId)
        {
            var m = await db.Meetings.Include(x => x.Attendees).FirstOrDefaultAsync(x => x.Id == meetingId, ct);
            if (m is null) return (SaveResult.NotFound, null, []);
            if (m.OrganizerId != userId) return (SaveResult.Forbidden, null, []);
            if (m.Status == MeetingStatus.Cancelled) return (SaveResult.Cancelled, null, []);
            List<string>? niks = null;
            if (m.RoomId is null && (req.AttendeeIds is not null || req.AttendeeNiks is not null))
            {
                var ids = req.AttendeeIds ?? [];
                niks = await db.Users.Where(u => ids.Contains(u.Id)).Select(u => u.Nik).ToListAsync(ct);
                niks.AddRange(req.AttendeeNiks ?? []);
            }
            var upd = new MeetingEndpoints.UpdateMeetingRequest(title, start, end, req.Location, req.Link, req.Notes, niks, req.ReminderMinutes ?? 0);
            var (res, added, _) = await MeetingEndpoints.ApplyUpdateAsync(db, meetingId, userId, upd, ct);
            if (res != MeetingEndpoints.UpdateResult.Applied) return (SaveResult.InvalidRange, null, []);
            invited = added;
            ev = await db.CalendarEvents.FirstAsync(e => e.Id == id, ct);
            ApplyDetails(ev, req, title, start, end);
            ev.UpdatedById = actedBy;
            if (CalendarVisibility.IsEventLevel(req.Visibility)) m.Visibility = req.Visibility!;
        }
        else
        {
            ApplyDetails(ev, req, title, start, end);
            ev.UpdatedById = actedBy;
            // Menambah peserta/grup ke event pribadi menjadikannya rapat.
            if (req.RoomId is not null || req.HasAttendees)
            {
                var (ok, ids) = await ResolveAttendeesAsync(db, userId, req, ct);
                if (!ok) return (SaveResult.NotRoomMember, null, []);
                var meeting = new Meeting
                {
                    Id = Guid.NewGuid(), Title = title, StartAt = start, EndAt = end, OrganizerId = userId, Status = "aktif",
                    RoomId = req.RoomId, Location = ev.Location, Link = ev.Link, Agenda = ev.Notes,
                    ReminderMinutes = ev.ReminderMinutes, Visibility = ev.Visibility, CreatedAt = DateTime.UtcNow, SentById = actedBy,
                };
                db.Meetings.Add(meeting);
                foreach (var uid in ids)
                    db.MeetingAttendees.Add(new MeetingAttendee
                    {
                        Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = uid, Required = true,
                        RsvpStatus = uid == userId ? "accept" : "pending", RespondedAt = uid == userId ? DateTime.UtcNow : null,
                    });
                AddReminders(db, meeting.Id, start, ev.ReminderMinutes, ids);
                ev.MeetingId = meeting.Id;
                ev.RoomId = req.RoomId;
                ev.Type = "rapat";
                invited = ids.Where(x => x != userId).ToList();
            }
        }
        await db.SaveChangesAsync(ct);
        return (SaveResult.Ok, ev, invited);
    }

    /// Owner deletes: a personal event is removed; a meeting is cancelled (attendees returned for notification).
    public static async Task<(SaveResult, string Title, IReadOnlyList<Guid> Attendees)> DeleteEventAsync(IPopDbContext db, Guid id, Guid userId, CancellationToken ct)
    {
        var ev = await db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev is null) return (SaveResult.NotFound, "", []);
        if (ev.OwnerId != userId) return (SaveResult.Forbidden, "", []);
        IReadOnlyList<Guid> attendees = [];
        if (ev.MeetingId is { } meetingId)
        {
            var m = await db.Meetings.Include(x => x.Attendees).FirstOrDefaultAsync(x => x.Id == meetingId, ct);
            if (m is not null)
            {
                if (m.OrganizerId != userId) return (SaveResult.Forbidden, "", []);
                m.Status = MeetingStatus.Cancelled;
                attendees = m.Attendees.Where(a => a.UserId != userId).Select(a => a.UserId).ToList();
                db.MeetingReminders.RemoveRange(await db.MeetingReminders.Where(x => x.MeetingId == meetingId && x.SentAt == null).ToListAsync(ct));
            }
            db.CalendarEvents.RemoveRange(await db.CalendarEvents.Where(e => e.MeetingId == meetingId).ToListAsync(ct));
        }
        else db.CalendarEvents.Remove(ev);
        await db.SaveChangesAsync(ct);
        return (SaveResult.Ok, ev.Title, attendees);
    }

    private static async Task<(bool Ok, List<Guid> Ids)> ResolveAttendeesAsync(IPopDbContext db, Guid userId, SaveEventRequest req, CancellationToken ct)
    {
        var ids = new List<Guid> { userId };
        if (req.RoomId is { } roomId)
        {
            var members = await db.ChatRoomMembers.AsNoTracking()
                .Where(m => m.RoomId == roomId && m.LeftAt == null).Select(m => m.UserId).ToListAsync(ct);
            if (!members.Contains(userId)) return (false, []);
            ids.AddRange(members);
        }
        if (req.AttendeeIds is { Count: > 0 })
            ids.AddRange(await db.Users.AsNoTracking().Where(u => req.AttendeeIds.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct));
        if (req.AttendeeNiks is { Count: > 0 })
            ids.AddRange(await db.Users.AsNoTracking().Where(u => req.AttendeeNiks.Contains(u.Nik) && u.IsActive).Select(u => u.Id).ToListAsync(ct));
        return (true, ids.Distinct().ToList());
    }

    private static void AddReminders(IPopDbContext db, Guid meetingId, DateTime start, int? minutes, IEnumerable<Guid> userIds)
    {
        if (minutes is not > 0) return;
        var sendAt = start.AddMinutes(-minutes.Value);
        if (sendAt <= DateTime.UtcNow) return;
        foreach (var uid in userIds)
            db.MeetingReminders.Add(new MeetingReminder { Id = Guid.NewGuid(), MeetingId = meetingId, UserId = uid, SendAt = sendAt });
    }

    /// Awaited (not `_ =`): the publish shares the request's DbContext with the LoadDetailAsync
    /// that follows, and a concurrent second operation on it threw after a successful save,
    /// so the client saw a 500 for an event that had in fact been created.
    internal static async Task NotifyInvitedAsync(IPopDbContext db, INotificationGatewayService? notifs, CalendarEvent ev, Guid organizerId,
        IReadOnlyList<Guid> invited, ILogger? log, CancellationToken ct, Guid? sentBy = null)
    {
        if (notifs is null || invited.Count == 0) return;
        var organizer = await db.Users.AsNoTracking().Where(u => u.Id == organizerId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "Someone";
        var sender = sentBy is { } sb ? await db.Users.AsNoTracking().Where(u => u.Id == sb).Select(u => u.Name).FirstOrDefaultAsync(ct) : null;
        var who = sender is null ? organizer : $"{organizer} (sent by {sender} on their behalf)";
        await MeetingEndpoints.PublishBestEffortAsync(notifs, log, "meeting.invited", $"Meeting invite: {ev.Title}",
            $"{who} invited you to \"{ev.Title}\" on {ev.StartAt.ToLocalTime():dd MMM, HH:mm}.",
            invited.Where(i => i != sentBy).ToList(), "normal", $"/calendar?event={ev.Id}&date={ev.StartAt.ToLocalTime():yyyy-MM-dd}");
        await CalendarDelegateEndpoints.ForwardInvitesAsync(db, notifs, log, invited, ev.Title, ev.StartAt, organizer, sentBy, ct);
    }

    public static async Task<CalendarEventDetailDto?> LoadDetailAsync(IPopDbContext db, Guid id, Guid viewerId, CancellationToken ct)
    {
        var ev = await db.CalendarEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev is null) return null;
        List<CalendarPersonDto> people = [];
        string? status = null, roomName = null;
        var visibility = ev.Visibility;
        Guid? sentById = null;
        if (ev.MeetingId is { } mid)
        {
            people = await db.MeetingAttendees.AsNoTracking().Where(a => a.MeetingId == mid)
                .OrderBy(a => a.User.Name)
                .Select(a => new CalendarPersonDto(a.UserId, a.User.Nik, a.User.Name, a.RsvpStatus,
                    a.RespondedById != null ? db.Users.Where(u => u.Id == a.RespondedById).Select(u => u.Name).FirstOrDefault() : null))
                .ToListAsync(ct);
            var meta = await db.Meetings.AsNoTracking().Where(m => m.Id == mid).Select(m => new { m.Status, m.Visibility, m.SentById }).FirstOrDefaultAsync(ct);
            status = meta?.Status;
            if (meta is not null) { visibility = meta.Visibility; sentById = meta.SentById; }
        }
        // Someone given "See all details" or more by the owner — or by an invitee (the row belongs to the
        // organizer) — opens it; Private only for an "Act on my behalf" assistant. Only the owner's
        // edit/full assistants may change it; an invitee's full assistant answers for them.
        string? delegateLevel = null;
        Guid? delegateFor = null;
        if (ev.OwnerId != viewerId)
        {
            var principals = people.Select(p => p.UserId).Append(ev.OwnerId).Where(id => id != viewerId).Distinct().ToList();
            var levels = await CalendarAccess.LevelsAsync(db, viewerId, principals, ct);
            var allowed = levels.Where(l => CalendarAccess.Shown(visibility, l.Value, invited: false, self: false).Detail).ToList();
            var pick = allowed.Where(l => l.Key == ev.OwnerId).Concat(allowed.OrderByDescending(l => DelegateLevel.Rank(l.Value)))
                .Select(l => (KeyValuePair<Guid, string>?)l).FirstOrDefault();
            if (pick is { } p2) { delegateLevel = p2.Value; delegateFor = p2.Key; }
        }
        if (ev.OwnerId != viewerId && delegateFor is null && people.All(p => p.UserId != viewerId)) return null;
        if (ev.RoomId is { } rid) roomName = await db.ChatRooms.AsNoTracking().Where(r => r.Id == rid).Select(r => r.Name).FirstOrDefaultAsync(ct);
        var nameIds = new[] { ev.OwnerId, ev.CreatedById, ev.UpdatedById, sentById, delegateFor }.OfType<Guid>().Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => nameIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new { u.Name, u.Nik }, ct);
        string? NameOf(Guid? id) => id is { } x ? names.GetValueOrDefault(x)?.Name : null;
        var canEdit = ev.OwnerId == viewerId || (delegateFor == ev.OwnerId && DelegateLevel.CanEdit(delegateLevel));
        return new CalendarEventDetailDto(ev.Id, ev.Title, ev.StartAt, ev.EndAt, ev.AllDay, ev.Location, ev.Link, ev.Notes,
            ev.ReminderMinutes, ev.RoomId, roomName, ev.MeetingId, status, NameOf(ev.OwnerId) ?? "", canEdit, people, visibility,
            names.GetValueOrDefault(ev.OwnerId)?.Nik, NameOf(ev.CreatedById), NameOf(sentById), NameOf(ev.UpdatedById), delegateLevel,
            delegateFor is { } df ? names.GetValueOrDefault(df)?.Nik : null, NameOf(delegateFor),
            ev.SeriesId, ev.Recurrence, CalendarRecurrence.Parse(ev.Recurrence)?.Describe(ev.StartAt.ToLocalTime()));
    }

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

    /// One calendar entry from any module. Source = filter group (personal/meetings/chat/projects/mail/news);
    /// Kind = sub-type for icon/colour. Url = where clicking goes (null → open the event drawer).
    public record CalendarFeedItem(string Id, string Source, string Kind, string Title, DateTime StartAt, DateTime EndAt, bool AllDay,
        string? Url, string? Subtitle, Guid? EventId, Guid? MeetingId, string? Rsvp, bool CanEdit, string? Location, string? Link,
        string? Visibility = null);
    public record SaveEventRequest(string? Title, DateTime StartAt, DateTime EndAt, bool AllDay = false, string? Location = null,
        string? Link = null, string? Notes = null, int? ReminderMinutes = null, IReadOnlyList<Guid>? AttendeeIds = null,
        Guid? RoomId = null, string? Type = null, IReadOnlyList<string>? AttendeeNiks = null, string? Visibility = null,
        string? OwnerNik = null, string? Recurrence = null)
    {
        public bool HasAttendees => AttendeeIds is { Count: > 0 } || AttendeeNiks is { Count: > 0 };
    }
    /// RespondedBy = the delegate who answered on this person's behalf.
    public record CalendarPersonDto(Guid UserId, string Nik, string Name, string Rsvp, string? RespondedBy = null);
    public record CalendarEventDetailDto(Guid Id, string Title, DateTime StartAt, DateTime EndAt, bool AllDay, string? Location,
        string? Link, string? Notes, int? ReminderMinutes, Guid? RoomId, string? RoomName, Guid? MeetingId, string? MeetingStatus,
        string OwnerName, bool CanEdit, IReadOnlyList<CalendarPersonDto> Attendees, string Visibility = CalendarVisibility.Busy,
        string? OwnerNik = null, string? CreatedBy = null, string? SentBy = null, string? UpdatedBy = null, string? DelegateLevel = null,
        string? DelegateForNik = null, string? DelegateForName = null, Guid? SeriesId = null, string? Recurrence = null, string? RecurrenceText = null);
}
