using IPop.Application.Calls;
using IPop.Application.Calendar;
using IPop.Application.Rbac;
using IPop.Infrastructure.Calendar;
using IPop.Infrastructure.Rbac;
using IPop.Application.Dashboard;
using IPop.Core.Presence;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Dashboard;

/// Aggregates the v6 Dashboard in one call. Every list is capped small — the page is a
/// summary; each card links through to its module for the full view.
public sealed class DashboardService(IPopDbContext db, ILiveKitTokenService? liveKit = null, IPermissionEvaluator? permissions = null) : IDashboardService
{
    public async Task<DashboardDto?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var me = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (me is null) return null;

        var now = DateTime.UtcNow;
        var tz = ResolveTimeZone(me.TimeZoneId);
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(now, tz).Date;
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localToday, tz);
        var dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(localToday.AddDays(1), tz);

        // ── Mail ──
        var mailUnread = await db.MailRecipients.AsNoTracking()
            .CountAsync(r => r.UserId == userId && r.Folder == "inbox" && !r.IsRead && !r.IsMuted, ct);
        var mailInbox = await db.MailRecipients.AsNoTracking()
            .CountAsync(r => r.UserId == userId && r.Folder == "inbox", ct);

        // ── Chat ── (same unread rule as ChatBadgeProvider)
        var myRooms = db.ChatRoomMembers.AsNoTracking().Where(m => m.UserId == userId && m.LeftAt == null).Select(m => m.RoomId);
        var settings = await db.ChatRoomUserSettings.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);
        var settingsByRoom = settings.ToDictionary(s => s.RoomId);
        var rooms = await db.ChatRooms.AsNoTracking()
            .Where(r => r.IsActive && myRooms.Contains(r.Id))
            .OrderByDescending(r => r.LastMessageAt)
            .Select(r => new { r.Id, r.Type, r.Name, r.AvatarColor, r.LastMessageAt })
            .ToListAsync(ct);
        rooms = rooms.Where(r => !(settingsByRoom.TryGetValue(r.Id, out var s) && s.IsArchived)).ToList();

        var roomIds = rooms.Select(r => r.Id).ToList();
        var unreadMsgs = await db.ChatMessages.AsNoTracking()
            .Where(m => roomIds.Contains(m.RoomId) && m.ThreadParentId == null && m.SenderId != userId && m.Type != "system")
            .Select(m => new { m.RoomId, m.CreatedAt })
            .ToListAsync(ct);
        int UnreadIn(Guid roomId)
        {
            settingsByRoom.TryGetValue(roomId, out var s);
            if (s is { IsMuted: true } && (s.MuteExpiresAt is null || s.MuteExpiresAt > now)) return 0;
            return unreadMsgs.Count(m => m.RoomId == roomId && (s?.LastReadMessageAt is null || m.CreatedAt > s.LastReadMessageAt));
        }
        var chatUnread = roomIds.Sum(UnreadIn);

        var topRooms = rooms.Take(4).ToList();
        var topIds = topRooms.Select(r => r.Id).ToList();
        var lastMsgs = await db.ChatMessages.AsNoTracking()
            .Where(m => topIds.Contains(m.RoomId) && m.ThreadParentId == null && m.Type != "system")
            .GroupBy(m => m.RoomId)
            .Select(g => g.OrderByDescending(m => m.CreatedAt).Select(m => new { m.RoomId, m.Content, m.IsDeleted, m.CreatedAt }).First())
            .ToListAsync(ct);
        var dmPeers = await db.ChatRoomMembers.AsNoTracking()
            .Where(m => topIds.Contains(m.RoomId) && m.UserId != userId)
            .Select(m => new { m.RoomId, m.User.Name, m.User.Nik })
            .ToListAsync(ct);
        var chats = topRooms.Select(r =>
        {
            var last = lastMsgs.FirstOrDefault(m => m.RoomId == r.Id);
            var peer = r.Type == "dm" ? dmPeers.FirstOrDefault(p => p.RoomId == r.Id) : null;
            var name = r.Type == "dm" ? peer?.Name ?? "Direct message" : r.Name ?? "Group";
            var text = last is null ? null : last.IsDeleted ? "Message deleted" : Snip(last.Content, 80);
            return new DashboardChatItem(r.Id, r.Type, name, text, last?.CreatedAt ?? r.LastMessageAt, UnreadIn(r.Id), r.AvatarColor, peer?.Nik);
        }).ToList();

        // ── Tasks ──
        var openTasks = await db.ProjectTasks.AsNoTracking()
            .Where(t => t.Status != "done" && !t.IsTemplate && t.Project.IsActive
                && t.Assignees.Any(a => a.UserId == userId))
            .Select(t => new { t.Id, t.ProjectId, t.Title, ProjectName = t.Project.Name, t.DueDate })
            .ToListAsync(ct);
        var sortedTasks = openTasks.OrderBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.Title).ToList();
        bool IsOverdue(DateTime? due) => due is { } d && d.Date < localToday;
        var overdue = sortedTasks.Count(t => IsOverdue(t.DueDate));
        var myTasks = sortedTasks.Take(5)
            .Select(t => new DashboardTaskItem(t.Id, t.ProjectId, t.Title, t.ProjectName, t.DueDate, IsOverdue(t.DueDate)))
            .ToList();

        // ── Projects ──
        var isAdmin = await db.UserRoles.AsNoTracking().AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct);
        var memberOf = db.ProjectMembers.AsNoTracking().Where(pm => pm.UserId == userId).Select(pm => pm.ProjectId);
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.IsActive && p.Status != "completed" && (isAdmin || p.OwnerId == userId || memberOf.Contains(p.Id)))
            .OrderByDescending(p => p.UpdatedAt)
            .Take(4)
            .Select(p => new DashboardProjectItem(p.Id, p.Name, p.Status,
                p.Tasks.Count(t => !t.IsTemplate && t.ParentTaskId == null && t.Status == "done"),
                p.Tasks.Count(t => !t.IsTemplate && t.ParentTaskId == null),
                p.Progress))
            .ToListAsync(ct);

        // ── Meetings ──
        var myMeetings = db.Meetings.AsNoTracking()
            .Where(m => m.Status != IPop.Core.Entities.MeetingStatus.Cancelled && (m.OrganizerId == userId || m.Attendees.Any(a => a.UserId == userId && a.RsvpStatus != "decline")));
        var next = await myMeetings.Where(m => m.EndAt >= now).OrderBy(m => m.StartAt)
            .Select(m => new { m.Id, m.Title, m.StartAt }).FirstOrDefaultAsync(ct);
        var access = await CalendarFeedAccess.ForAsync(permissions ?? new PermissionEvaluator(db), userId, ct);
        var feed = await CalendarFeedService.BuildAsync(db, userId, dayStartUtc, dayEndUtc, access, ct);
        // Month feeds pad date-only queries. Match wall-calendar dates for this daily agenda.
        var agenda = feed.Where(i => i.AllDay
                ? i.StartAt.Date <= localToday && i.EndAt.Date > localToday
                : i.StartAt < dayEndUtc && i.EndAt > dayStartUtc)
            .OrderBy(i => i.AllDay ? 0 : 1).ThenBy(i => i.StartAt).ThenBy(i => i.Title)
            .Select(i => new DashboardAgendaItem(
                i.Kind switch { "task-overdue" => "task", "ticket-due" => "ticket", "company-holiday" => "holiday", _ => i.Kind },
                Guid.Parse(i.Id[(i.Id.IndexOf(':') + 1)..]), i.Title, i.Subtitle ?? "Calendar",
                i.AllDay ? null : i.StartAt, i.AllDay ? null : i.EndAt,
                !i.AllDay && i.EndAt < now,
                i.Kind == "task-overdue" || (!i.AllDay && (i.Source is "projects" or "tickets") && i.EndAt < now),
                i.Url ?? (i.EventId is { } eventId ? $"/calendar?event={eventId}" : $"/calendar?date={localToday:yyyy-MM-dd}"),
                i.MeetingId is { } meetingId ? $"meeting-{meetingId:N}" : null, i.AllDay, i.Source))
            .ToList();

        // ── News ──
        var visibleNews = await db.NewsPosts.AsNoTracking()
            .Where(n => n.Status == "published" && (n.PublishAt == null || n.PublishAt <= now))
            .OrderByDescending(n => n.PublishedAt)
            .Take(30)
            .Select(n => new
            {
                n.Id, n.Type, n.Title, n.PublishedAt, n.RequiresAcknowledgement,
                Acked = db.NewsAcknowledgements.Any(a => a.NewsPostId == n.Id && a.UserId == userId),
                Targets = n.Targets.Select(t => new { t.Kind, t.Value }).ToList(),
            })
            .ToListAsync(ct);
        var deptCode = me.DepartmentId is null ? null
            : await db.Departments.AsNoTracking().Where(d => d.Id == me.DepartmentId).Select(d => d.Code).FirstOrDefaultAsync(ct);
        var roles = await db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId).Select(ur => ur.Role.Name).ToListAsync(ct);
        var myProjectIds = (await memberOf.ToListAsync(ct)).Select(g => g.ToString()).ToHashSet();
        bool Visible(IEnumerable<(string Kind, string? Value)> targets)
        {
            var list = targets.ToList();
            return list.Count == 0 || list.Any(t => t.Kind switch
            {
                "all" => true,
                "user" => t.Value == me.Nik,
                "department" => t.Value is not null && t.Value == deptCode,
                "role" => t.Value is not null && roles.Contains(t.Value),
                "project" => t.Value is not null && myProjectIds.Contains(t.Value),
                _ => false,
            });
        }
        var news = visibleNews.Where(n => Visible(n.Targets.Select(t => (t.Kind, t.Value)))).ToList();
        var ackPending = news.Where(n => n.RequiresAcknowledgement && !n.Acked).ToList();

        // ── Needs your attention ──
        var attention = new List<DashboardAttentionItem>();
        attention.AddRange(ackPending.Select(n => new DashboardAttentionItem("ack",
            $"Acknowledge “{n.Title}”", "Memo requires your acknowledgement", "Read", $"/news?post={n.Id}")));

        var missed = await db.CallHistories.AsNoTracking()
            .Where(h => h.PerformerId == userId && h.Direction == "incoming" && h.Result == "missed" && h.StartedAt >= now.AddDays(-2))
            .OrderByDescending(h => h.StartedAt)
            .Take(3)
            .Select(h => new
            {
                h.StartedAt,
                Caller = h.Call.Participants.Where(p => p.UserId != userId).Select(p => new { p.User.Name, p.User.Nik }).FirstOrDefault(),
            })
            .ToListAsync(ct);
        attention.AddRange(missed.Where(m => m.Caller is not null).Select(m => new DashboardAttentionItem("missed-call",
            $"Missed call from {m.Caller!.Name}", LocalLabel(m.StartedAt, tz, localToday), "Call back", "/calls", m.Caller.Nik)));

        var mentions = await db.ChatMentions.AsNoTracking()
            .Where(x => x.UserId == userId && x.Message.SenderId != userId && !x.Message.IsDeleted && x.CreatedAt >= now.AddDays(-7))
            .OrderByDescending(x => x.CreatedAt)
            .Take(10)
            .Select(x => new { x.Message.RoomId, x.Message.CreatedAt, x.Message.Content, Sender = x.Message.Sender.Name, Room = x.Message.Room.Name, x.Message.Room.Type })
            .ToListAsync(ct);
        attention.AddRange(mentions
            .Where(x => !(settingsByRoom.TryGetValue(x.RoomId, out var s) && s.LastReadMessageAt is { } lr && x.CreatedAt <= lr))
            .Take(3)
            .Select(x => new DashboardAttentionItem("mention",
                x.Type == "group" && x.Room is not null ? $"{FirstName(x.Sender)} mentioned you in {x.Room}" : $"{FirstName(x.Sender)} mentioned you",
                Snip(x.Content, 90) ?? "", "Reply", $"/chat/{x.RoomId}")));

        // Notes mentions / comment replies arrive as gateway notifications; unread ones still need a look.
        var noteNotes = await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.UserId == userId && d.ReadAt == null && d.CreatedAt >= now.AddDays(-7)
                && (d.Request.EventType == "note.mention" || d.Request.EventType == "note.comment"))
            .OrderByDescending(d => d.CreatedAt)
            .Take(3)
            .Select(d => new { d.Request.Title, d.Request.Body, d.Request.TargetUrl, d.CreatedAt })
            .ToListAsync(ct);
        attention.AddRange(noteNotes.Select(n => new DashboardAttentionItem("note", n.Title,
            $"Notes · {LocalLabel(n.CreatedAt, tz, localToday)}", "Open", n.TargetUrl ?? "/notes")));

        attention.AddRange(sortedTasks.Where(t => IsOverdue(t.DueDate)).Take(3).Select(t => new DashboardAttentionItem("overdue",
            $"“{t.Title}” is overdue", $"{t.ProjectName} · due {t.DueDate:dd MMM}", "Open", $"/projects/{t.ProjectId}?task={t.Id}")));

        var missedCount = await db.CallHistories.AsNoTracking()
            .CountAsync(h => h.PerformerId == userId && h.Direction == "incoming" && h.Result == "missed" && h.StartedAt >= now.AddDays(-2), ct);

        // ── Notes ── pages I visited lately, topped up with ones I own.
        var visited = await db.NoteVisits.AsNoTracking()
            .Where(v => v.UserId == userId && db.NotePages.Any(p => p.Id == v.PageId && p.DeletedAt == null))
            .OrderByDescending(v => v.VisitedAt)
            .Take(4)
            .Select(v => v.PageId)
            .ToListAsync(ct);
        var owned = await db.NotePages.AsNoTracking()
            .Where(p => p.OwnerId == userId && p.DeletedAt == null && !p.IsGuide && !visited.Contains(p.Id))
            .OrderByDescending(p => p.UpdatedAt)
            .Take(4)
            .Select(p => p.Id)
            .ToListAsync(ct);
        var noteIds = visited.Concat(owned).Take(4).ToList();
        var favs = await db.NoteFavorites.AsNoTracking().Where(f => f.UserId == userId && noteIds.Contains(f.PageId)).Select(f => f.PageId).ToListAsync(ct);
        var notePages = await db.NotePages.AsNoTracking()
            .Where(p => noteIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Title, p.Emoji, p.UpdatedAt, p.UpdatedById, Editor = db.Users.Where(u => u.Id == p.UpdatedById).Select(u => u.Name).FirstOrDefault() })
            .ToListAsync(ct);
        var notes = noteIds.Select(id => notePages.FirstOrDefault(p => p.Id == id)).Where(p => p is not null)
            .Select(p => new DashboardNoteItem(p!.Id, string.IsNullOrWhiteSpace(p.Title) ? "Untitled" : p.Title, p.Emoji,
                p.UpdatedById is null || p.UpdatedById == userId ? "You" : FirstName(p.Editor), p.UpdatedAt, favs.Contains(p.Id)))
            .ToList();

        // ── Files ── running jobs, plus ones that finished in the last day.
        var transfers = await db.FileOperations.AsNoTracking()
            .Where(o => o.UserId == userId && (o.Status == "Queued" || o.Status == "Running" || o.FinishedAt >= now.AddDays(-1)))
            .OrderByDescending(o => o.CreatedAt)
            .Take(2)
            .Select(o => new DashboardTransferItem(o.Id, o.Kind, o.Status, o.TotalFiles, o.CompletedFiles, o.FailedFiles,
                o.DestServer, o.DestDir, o.FinishedAt))
            .ToListAsync(ct);

        // ── Team online ──
        var awayCutoff = now - PresenceStatus.OfflineAfter;
        var present = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Id != userId && u.PresenceLastSeenAt > awayCutoff)
            .Select(u => new { u.Nik, u.Name, u.PresenceLastSeenAt, u.PresenceManualStatus, u.PresenceIsIdle })
            .ToListAsync(ct);
        var online = present
            .Select(u => new DashboardPersonItem(u.Nik, u.Name, PresenceStatus.ComputeStatus(u.PresenceLastSeenAt, u.PresenceManualStatus, now, u.PresenceIsIdle)))
            .Where(p => p.Status != "off")
            .OrderBy(p => p.Status == "on" ? 0 : p.Status == "busy" ? 1 : 2).ThenBy(p => p.Name)
            .ToList();

        // ── System status ──
        var status = new List<DashboardStatusItem>
        {
            liveKit is { IsConfigured: true }
                ? new("Chat & Calls", "ok", "LiveKit SFU")
                : new("Chat & Calls", "ok", "Peer-to-peer calls"),
        };
        var outboxFailing = await db.EmailOutbox.AsNoTracking().CountAsync(o => o.SentAt == null && o.LastError != null, ct);
        status.Add(outboxFailing > 0
            ? new("Mail", "warn", $"{outboxFailing} email(s) retrying")
            : new("Mail", "ok", "Operational"));
        var lastRun = await db.HrisSyncRuns.AsNoTracking().OrderByDescending(r => r.StartedAt)
            .Select(r => new { r.StartedAt, r.Succeeded }).FirstOrDefaultAsync(ct);
        status.Add(lastRun is null
            ? new("HRIS sync", "unknown", "No runs yet")
            : new("HRIS sync", lastRun.Succeeded ? "ok" : "warn", "Last run " + LocalLabel(lastRun.StartedAt, tz, localToday)));
        var nas = await db.SynologyCredentials.AsNoTracking().Where(c => c.UserId == userId)
            .Select(c => new { c.Server, c.IsInvalid }).ToListAsync(ct);
        status.Add(nas.Count == 0
            ? new("Synology NAS", "unknown", "Not connected")
            : new("Synology NAS", nas.Any(n => n.IsInvalid) ? "warn" : "ok",
                $"{nas.Count(n => !n.IsInvalid)} of {nas.Count} connected"));

        return new DashboardDto(
            IPop.Application.PersonName.Display(me.Name) is { Length: > 0 } full ? full : "there",
            new DashboardKpis(mailUnread, mailInbox, chatUnread, rooms.Count, openTasks.Count, overdue,
                next?.StartAt, next?.Title, next?.Id, ackPending.Count, missedCount),
            agenda,
            attention,
            myTasks,
            projects,
            chats,
            news.Take(3).Select(n => new DashboardNewsItem(n.Id, n.Type, n.Title, n.PublishedAt, n.RequiresAcknowledgement && !n.Acked)).ToList(),
            online.Take(10).ToList(),
            online.Count,
            status,
            notes,
            transfers,
            isAdmin);
    }

    private static string FirstName(string? name) =>
        IPop.Application.PersonName.Short(name) is { Length: > 0 } s ? s : "there";

    private static string? Snip(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max].TrimEnd() + "…";

    private static string LocalLabel(DateTime utc, TimeZoneInfo tz, DateTime localToday)
    {
        var l = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return l.Date == localToday ? $"Today, {l:HH:mm}"
            : l.Date == localToday.AddDays(-1) ? $"Yesterday, {l:HH:mm}"
            : l.ToString("dd MMM, HH:mm");
    }

    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        foreach (var candidate in new[] { id, "SE Asia Standard Time", "Asia/Jakarta" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }
}
