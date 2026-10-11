using IPop.Application.Calendar;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Calendar;

/// Shared calendar aggregation, including module and news-target visibility.
public static class CalendarFeedService
{
    public static async Task<List<CalendarFeedItem>> BuildAsync(IPopDbContext db, Guid userId, DateTime from, DateTime to, CalendarFeedAccess access, CancellationToken ct)
    {
        var items = new List<CalendarFeedItem>();
        // Tanggal all-day disimpan sebagai tengah malam UTC; beri kelonggaran sehari agar zona waktu lokal tidak memotongnya.
        var dayFrom = from.Date.AddDays(-1);
        var dayTo = to.Date.AddDays(1);

        // 1) Event pribadi (tanpa rapat).
        var personal = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.OwnerId == userId && e.MeetingId == null && e.StartAt < to && e.EndAt > from)
            .ToListAsync(ct);
        items.AddRange(personal.Select(e => e.Source switch
        {
            // Mirrored from iDesk: read-only here, managed there.
            CalendarSources.IdeskZoom => new CalendarFeedItem($"event:{e.Id}", "idesk", "idesk-zoom", e.Title, e.StartAt, e.EndAt, e.AllDay,
                null, e.Location, e.Id, null, null, false, e.Location, e.Link, e.Visibility),
            CalendarSources.IdeskVisit => new CalendarFeedItem($"event:{e.Id}", "idesk", "idesk-visit", e.Title, e.StartAt, e.EndAt, e.AllDay,
                null, "ICT visit", e.Id, null, null, false, e.Location, null, e.Visibility),
            _ => new CalendarFeedItem($"event:{e.Id}", "personal", e.Type == "rapat" ? "meeting" : "event", e.Title, e.StartAt, e.EndAt, e.AllDay,
                null, null, e.Id, null, null, true, e.Location, e.Link, e.Visibility),
        }));

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
        if (access.Tickets)
        {
            var tickets = await db.Tickets.AsNoTracking()
                .Where(x => x.UserId == userId && x.RemovedAt == null && x.Status != "CLOSED" && x.Status != "CANCELLED" && x.Status != "RESOLVED"
                    && x.SlaTarget != null && x.SlaTarget >= from && x.SlaTarget < to)
                .Select(x => new { x.Id, x.Title, x.TicketNumber, x.SlaTarget })
                .ToListAsync(ct);
            items.AddRange(tickets.Select(x => new CalendarFeedItem(
                $"ticket:{x.Id}", "tickets", "ticket-due", x.Title, x.SlaTarget!.Value.AddMinutes(-30), x.SlaTarget!.Value, false,
                $"/tickets/{x.Id}", x.TicketNumber is null ? "Ticket due" : $"Ticket {x.TicketNumber} due", null, null, null, false, null, null)));
        }

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

}
