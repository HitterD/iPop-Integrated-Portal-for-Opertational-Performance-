using IPop.Core.Entities;
using IPop.Api.Endpoints;
using IPop.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class DashboardAndSearchTests
{
    [Fact]
    public async Task SearchService_ReturnsGroupedResults_AcrossPeopleProjectsNewsMail()
    {
        using var t = new TestDb();
        var me = t.AddUser("USER_ME");
        var colleague = t.AddUser("BOB_JONES");
        colleague.Name = "Bob Jones";

        var project = t.AddProject("Titan Project", me.Id);
        t.Db.ProjectTasks.Add(new ProjectTask
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == project.Id).Id,
            Title = "Titan Task Alpha",
            Status = "todo",
            CreatedById = me.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        t.Db.NewsPosts.Add(new NewsPost
        {
            Id = Guid.NewGuid(),
            Type = "news",
            Title = "Titan Launch Announcement",
            Body = "Details about Titan",
            Status = "published",
            AuthorId = me.Id,
            PublishedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var mail = new Mail
        {
            Id = Guid.NewGuid(),
            SenderId = colleague.Id,
            Subject = "Titan Update Briefing",
            Body = "Hello Titan team",
            Status = "sent",
            CreatedAt = DateTime.UtcNow
        };
        t.Db.Mails.Add(mail);
        t.Db.MailRecipients.Add(new MailRecipient
        {
            Id = Guid.NewGuid(),
            MailId = mail.Id,
            UserId = me.Id,
            RecipientType = "to",
            Folder = "inbox",
            CreatedAt = DateTime.UtcNow
        });

        await t.Db.SaveChangesAsync();

        var searchSvc = new SearchService(t.Verify());
        var results = await searchSvc.SearchAsync(me.Id, "Titan", default);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Group == "Projects" && r.Title == "Titan Project");
        Assert.Contains(results, r => r.Group == "Tasks" && r.Title == "Titan Task Alpha");
        Assert.Contains(results, r => r.Group == "News & Memos" && r.Title == "Titan Launch Announcement");
        Assert.Contains(results, r => r.Group == "Mail" && r.Title == "Titan Update Briefing");
    }

    [Fact]
    public async Task SearchService_News_RespectsAudienceAndSchedule()
    {
        using var t = new TestDb();
        var me = t.AddUser("SRCH_ME");
        var author = t.AddUser("SRCH_AUTH");
        NewsPost Post(string title, DateTime? publishAt = null)
        {
            var p = new NewsPost
            {
                Id = Guid.NewGuid(), Type = "news", Title = title, Body = "b", Status = "published",
                AuthorId = author.Id, PublishAt = publishAt, PublishedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            t.Db.NewsPosts.Add(p);
            return p;
        }
        Post("Orion for all");
        var scheduled = Post("Orion scheduled", DateTime.UtcNow.AddDays(2));
        var targeted = Post("Orion for someone else");
        t.Db.NewsTargets.Add(new NewsTarget { Id = Guid.NewGuid(), NewsPostId = targeted.Id, Kind = "user", Value = author.Nik });
        var mine = Post("Orion for me");
        t.Db.NewsTargets.Add(new NewsTarget { Id = Guid.NewGuid(), NewsPostId = mine.Id, Kind = "user", Value = me.Nik });
        await t.Db.SaveChangesAsync();

        var titles = (await new SearchService(t.Verify()).SearchAsync(me.Id, "Orion", default))
            .Where(r => r.Group == "News & Memos").Select(r => r.Title).OrderBy(x => x).ToList();

        Assert.Equal(["Orion for all", "Orion for me"], titles);
    }

    [Fact]
    public async Task DashboardService_AggregatesKpisTasksAttentionAndAgenda()
    {
        using var t = new TestDb();
        var me = t.AddUser("DASH_ME");
        me.Name = "Rina Wijaya";
        me.TimeZoneId = "UTC";
        var bob = t.AddUser("DASH_BOB");
        bob.PresenceLastSeenAt = DateTime.UtcNow;

        var project = t.AddProject("Kopi Line 3", me.Id);
        var overdue = new ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == project.Id).Id, Title = "Calibrate roaster", Status = "todo",
            DueDate = DateTime.UtcNow.Date.AddDays(-2), CreatedById = me.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var done = new ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == project.Id).Id, Title = "Order beans", Status = "done",
            CreatedById = me.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.ProjectTasks.AddRange(overdue, done);
        t.Db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = overdue.Id, UserId = me.Id, AssignedAt = DateTime.UtcNow });
        t.Db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = done.Id, UserId = me.Id, AssignedAt = DateTime.UtcNow });

        t.Db.NewsPosts.Add(new NewsPost
        {
            Id = Guid.NewGuid(), Type = "memo", Title = "Safety memo", Body = "Read me", Status = "published",
            RequiresAcknowledgement = true, AuthorId = bob.Id, PublishedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        // In progress (not +30min): a future start near midnight UTC would fall on tomorrow's agenda.
        var start = DateTime.UtcNow.AddMinutes(-5);
        t.Db.Meetings.Add(new Meeting
        {
            Id = Guid.NewGuid(), Title = "Line 3 standup", StartAt = start, EndAt = start.AddMinutes(30),
            OrganizerId = me.Id, CreatedAt = DateTime.UtcNow,
        });

        var mail = new Mail { Id = Guid.NewGuid(), SenderId = bob.Id, Subject = "Hi", Body = "x", Status = "sent", CreatedAt = DateTime.UtcNow };
        t.Db.Mails.Add(mail);
        t.Db.MailRecipients.Add(new MailRecipient { Id = Guid.NewGuid(), MailId = mail.Id, UserId = me.Id, RecipientType = "to", Folder = "inbox", CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();

        var dto = await new IPop.Infrastructure.Dashboard.DashboardService(t.Verify()).GetAsync(me.Id);

        Assert.NotNull(dto);
        Assert.Equal("Rina Wijaya", dto.FirstName);
        Assert.Equal(1, dto.Kpis.MailUnread);
        Assert.Equal(1, dto.Kpis.OpenTasks);
        Assert.Equal(1, dto.Kpis.OverdueTasks);
        Assert.Equal("Line 3 standup", dto.Kpis.NextMeetingTitle);
        Assert.Equal(1, dto.Kpis.AckPending);
        Assert.Single(dto.MyTasks);
        Assert.True(dto.MyTasks[0].Overdue);
        Assert.Contains(dto.Attention, a => a.Kind == "ack" && a.Title.Contains("Safety memo"));
        Assert.Contains(dto.Attention, a => a.Kind == "overdue");
        Assert.Contains(dto.Agenda, a => a.Kind == "meeting" && a.Title == "Line 3 standup");
        Assert.Contains(dto.Projects, p => p.Name == "Kopi Line 3" && p.Done == 1 && p.Total == 2);
        Assert.Contains(dto.Online, p => p.Nik == "DASH_BOB" && p.Status == "on");
        Assert.Contains(dto.Status, s => s.Label == "HRIS sync");
    }

    [Fact]
    public async Task DashboardService_IncludesCalendarEventsNotesAndTransfers()
    {
        using var t = new TestDb();
        var me = t.AddUser("DASH_NOTES");
        me.TimeZoneId = "UTC";
        var now = DateTime.UtcNow;
        t.Db.CalendarEvents.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(), Title = "Dentist", OwnerId = me.Id, StartAt = now.Date.AddHours(23), EndAt = now.Date.AddHours(23).AddMinutes(30), CreatedAt = now,
        });
        var page = new NotePage { Id = Guid.NewGuid(), OwnerId = me.Id, Title = "Rencana Q4", Accent = "default", Font = "default", CreatedAt = now, UpdatedAt = now };
        t.Db.NotePages.Add(page);
        t.Db.NoteFavorites.Add(new NoteFavorite { PageId = page.Id, UserId = me.Id, CreatedAt = now });
        t.Db.FileOperations.Add(new FileOperation
        {
            Id = Guid.NewGuid(), UserId = me.Id, Kind = "move", SourceServer = "nas1", DestServer = "nas1", ItemsJson = "[]",
            DestDir = "/Shared/Finance", Status = "Running", TotalFiles = 12, CompletedFiles = 7, CreatedAt = now,
        });
        await t.Db.SaveChangesAsync();

        var dto = await new IPop.Infrastructure.Dashboard.DashboardService(t.Verify()).GetAsync(me.Id);

        Assert.NotNull(dto);
        Assert.Contains(dto.Agenda, a => a.Kind == "event" && a.Title == "Dentist");
        var note = Assert.Single(dto.Notes);
        Assert.Equal("Rencana Q4", note.Title);
        Assert.True(note.Favorite);
        Assert.Equal("You", note.EditedBy);
        var op = Assert.Single(dto.Transfers);
        Assert.Equal(7, op.CompletedFiles);
        Assert.Equal(0, dto.Kpis.MissedCalls);
        Assert.False(dto.IsAdmin);
    }

    [Fact]
    public async Task Dashboard_agenda_uses_calendar_sources_without_task_cap_and_respects_local_dates()
    {
        using var t = new TestDb();
        var me = t.AddUser("DASH_FEED");
        me.TimeZoneId = "Asia/Jakarta";
        var day = DateTime.UtcNow.AddHours(7).Date;
        var from = DateTime.SpecifyKind(day.AddHours(-7), DateTimeKind.Utc);
        var project = t.AddProject("Agenda project", me.Id);
        for (var i = 0; i < 4; i++)
        {
            var task = new ProjectTask { Id = Guid.NewGuid(), ProjectId = project.Id,
                GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == project.Id).Id,
                Title = $"Due task {i}", Status = "todo", DueDate = i == 0 ? day : from.AddHours(10 + i), CreatedById = me.Id };
            t.Db.ProjectTasks.Add(task);
            t.Db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = task.Id, UserId = me.Id });
        }
        t.Db.ProjectMilestones.Add(new ProjectMilestone { Id = Guid.NewGuid(), ProjectId = project.Id, Title = "Launch", DueDate = day });
        t.Db.WorkCalendarEntries.Add(new WorkCalendarEntry { Id = Guid.NewGuid(), ProjectId = project.Id,
            Date = day, Kind = "off", Title = "Team off", CreatedById = me.Id });
        t.Db.CompanyHolidays.AddRange(
            new CompanyHoliday { Id = Guid.NewGuid(), Date = day, Title = "Today holiday" },
            new CompanyHoliday { Id = Guid.NewGuid(), Date = day.AddDays(-1), Title = "Yesterday holiday" },
            new CompanyHoliday { Id = Guid.NewGuid(), Date = day.AddDays(1), Title = "Tomorrow holiday" });
        t.Db.Mails.Add(new Mail { Id = Guid.NewGuid(), SenderId = me.Id, Subject = "Send report", Body = "x",
            Status = "scheduled", ScheduledAt = from.AddHours(8) });
        t.Db.NewsPosts.Add(new NewsPost { Id = Guid.NewGuid(), AuthorId = me.Id, Title = "Read memo", Body = "x", Type = "memo",
            Status = "published", PublishedAt = from.AddHours(1), RequiresAcknowledgement = true, AckDueAt = from.AddHours(12) });
        t.Db.Tickets.Add(new Ticket { Id = Guid.NewGuid(), UserId = me.Id, Provider = "idesk", Origin = "IPOP", Status = "IN_PROGRESS",
            Priority = "HIGH", Module = "ict", ModuleName = "ICT", Category = "Printer", Title = "Fix printer", SlaTarget = from.AddHours(14) });
        await t.Db.SaveChangesAsync();

        var dto = (await new IPop.Infrastructure.Dashboard.DashboardService(t.Db, permissions: new FeedPermissions()).GetAsync(me.Id))!;
        var feed = await CalendarEndpoints.BuildFeedAsync(t.Db, me.Id, from, from.AddDays(1), CalendarEndpoints.FeedAccess.All, default);
        var expected = feed.Where(i => i.AllDay ? i.StartAt.Date <= day && i.EndAt.Date > day : i.StartAt < from.AddDays(1) && i.EndAt > from);
        Assert.Equal(expected.Select(i => i.Title).Order(), dto.Agenda.Select(i => i.Title).Order());
        Assert.Equal(4, dto.Agenda.Count(i => i.Kind == "task"));
        Assert.Contains(dto.Agenda, i => i.Kind == "milestone" && i.AllDay);
        Assert.Contains(dto.Agenda, i => i.Kind == "off" && i.Source == "projects");
        Assert.Contains(dto.Agenda, i => i.Kind == "scheduled-mail" && i.Source == "mail");
        Assert.Contains(dto.Agenda, i => i.Kind == "ack-due" && i.Source == "news");
        Assert.Contains(dto.Agenda, i => i.Kind == "memo");
        Assert.DoesNotContain(dto.Agenda, i => i.Title is "Yesterday holiday" or "Tomorrow holiday");
        var timed = Assert.Single(dto.Agenda, i => i.Title == "Due task 1");
        Assert.Equal(from.AddHours(11).AddMinutes(-30), timed.StartAt);
        Assert.Equal(from.AddHours(11), timed.EndAt);
        Assert.False(timed.AllDay);
        var dateOnly = Assert.Single(dto.Agenda, i => i.Title == "Due task 0");
        Assert.True(dateOnly.AllDay);
        Assert.Null(dateOnly.StartAt);
        var ticket = Assert.Single(dto.Agenda, i => i.Kind == "ticket");
        Assert.Equal(from.AddHours(14).AddMinutes(-30), ticket.StartAt);

        var denied = (await new IPop.Infrastructure.Dashboard.DashboardService(t.Db, permissions: new FeedPermissions(false)).GetAsync(me.Id))!;
        Assert.DoesNotContain(denied.Agenda, i => i.Source is "projects" or "mail" or "news" or "tickets");
        Assert.Contains(denied.Agenda, i => i.Kind == "holiday");
        var noTickets = await CalendarEndpoints.BuildFeedAsync(t.Db, me.Id, from, from.AddDays(1),
            CalendarEndpoints.FeedAccess.All with { Tickets = false }, default);
        Assert.DoesNotContain(noTickets, i => i.Source == "tickets");
    }

    [Fact]
    public async Task Feed_access_evaluates_tickets_read_and_dashboard_defaults_to_denied()
    {
        using var t = new TestDb();
        var me = t.AddUser("DASH_DENIED");
        me.TimeZoneId = "UTC";
        t.Db.Tickets.Add(new Ticket { Id = Guid.NewGuid(), UserId = me.Id, Provider = "idesk", Origin = "IPOP", Status = "OPEN",
            Priority = "HIGH", Module = "ict", ModuleName = "ICT", Category = "Printer", Title = "Private ticket", SlaTarget = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();
        var permissions = new FeedPermissions();
        var access = await CalendarEndpoints.FeedAccess.ForAsync(permissions, me.Id, default);
        Assert.True(access.Tickets);
        Assert.Contains("tickets.read", permissions.Checked);
        var dto = (await new IPop.Infrastructure.Dashboard.DashboardService(t.Db).GetAsync(me.Id))!;
        Assert.DoesNotContain(dto.Agenda, i => i.Source == "tickets");
    }

    private sealed class FeedPermissions(bool allowed = true) : IPop.Application.Rbac.IPermissionEvaluator
    {
        public List<string> Checked { get; } = [];
        public Task<bool> IsAllowedAsync(Guid userId, string permissionCode, string? departmentCode, CancellationToken ct = default)
        {
            Checked.Add(permissionCode);
            return Task.FromResult(allowed);
        }
    }
}
