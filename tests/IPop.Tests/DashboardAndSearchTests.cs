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

        // Presence changes re-read only this list; it must match the full dashboard's card.
        var online = await new IPop.Infrastructure.Dashboard.DashboardService(t.Verify()).OnlineAsync(me.Id);
        Assert.Equal(dto.OnlineCount, online.OnlineCount);
        Assert.Equal(dto.Online.Select(p => p.Nik), online.Online.Select(p => p.Nik));
        Assert.DoesNotContain(online.Online, p => p.Nik == "DASH_ME");
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
            // Published at the start of today (local), so it is never in the future whatever time the suite runs.
            Status = "published", PublishedAt = from.AddMinutes(1), RequiresAcknowledgement = true, AckDueAt = from.AddHours(12) });
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

    [Fact]
    public async Task Dashboard_chat_unread_counts_only_messages_after_each_rooms_read_mark()
    {
        using var t = new TestDb();
        var me = t.AddUser("DU_ME"); var b = t.AddUser("DU_B");
        var read = t.AddRoom("group", "Read"); var fresh = t.AddRoom("group", "Fresh");
        foreach (var r in new[] { read, fresh }) { t.AddMember(r.Id, me.Id); t.AddMember(r.Id, b.Id); }
        var now = DateTime.UtcNow;
        t.AddMessage(read.Id, b.Id, "old", at: now.AddMinutes(-30));
        t.AddMessage(read.Id, b.Id, "new", at: now.AddMinutes(-5));
        t.AddMessage(fresh.Id, b.Id, "a", at: now.AddMinutes(-3));
        t.AddMessage(fresh.Id, b.Id, "b", at: now.AddMinutes(-2));
        t.AddMessage(fresh.Id, me.Id, "mine", at: now.AddMinutes(-1));              // own messages never count
        t.Db.ChatRoomUserSettings.Add(new IPop.Core.Entities.ChatRoomUserSettings
            { UserId = me.Id, RoomId = read.Id, LastReadMessageAt = now.AddMinutes(-20) });
        t.Db.SaveChanges();

        var dto = await new IPop.Infrastructure.Dashboard.DashboardService(t.Verify()).GetAsync(me.Id);

        Assert.Equal(3, dto.Kpis.ChatUnread);   // 1 after the read mark + 2 in the never-read room
    }
    [Fact]
    public async Task Dashboard_missed_calls_show_count_and_newest_three_callers()
    {
        using var t = new TestDb();
        var me = t.AddUser("MC_ME"); me.TimeZoneId = "UTC";
        var now = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            var caller = t.AddUser($"MC_C{i}"); caller.Name = $"Caller {i}";
            var call = new CallRoom { Id = Guid.NewGuid(), CreatorId = caller.Id, Kind = "voice", Status = "ended",
                StartedAt = now.AddMinutes(-10 * (i + 1)), CreatedAt = now, UpdatedAt = now };
            t.Db.CallRooms.Add(call);
            t.Db.CallParticipants.Add(new CallParticipant { Id = Guid.NewGuid(), CallId = call.Id, UserId = caller.Id, Status = "left", JoinedAt = now });
            t.Db.CallParticipants.Add(new CallParticipant { Id = Guid.NewGuid(), CallId = call.Id, UserId = me.Id, Status = "missed", JoinedAt = now });
            t.Db.CallHistories.Add(new CallHistory { Id = Guid.NewGuid(), CallId = call.Id, PerformerId = me.Id, Direction = "incoming",
                Result = "missed", StartedAt = call.StartedAt, EndedAt = call.StartedAt });
        }
        await t.Db.SaveChangesAsync();

        var dto = (await new IPop.Infrastructure.Dashboard.DashboardService(t.Verify()).GetAsync(me.Id))!;

        Assert.Equal(4, dto.Kpis.MissedCalls);
        Assert.Equal(["Missed call from Caller 0", "Missed call from Caller 1", "Missed call from Caller 2"],
            dto.Attention.Where(a => a.Kind == "missed-call").Select(a => a.Title));
    }

    [Fact]
    public async Task Dashboard_reads_each_shared_fact_once_and_reuses_system_status()
    {
        using var t = new TestDb();
        var me = t.AddUser("QC_ME");
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<IPop.Infrastructure.Persistence.IPopDbContext>()
            .UseSqlite(t.Db.Database.GetDbConnection()).AddInterceptors(counter).Options;
        var cache = new IPop.Infrastructure.Dashboard.DashboardSharedCache();

        await using (var db = new IPop.Infrastructure.Persistence.IPopDbContext(options))
            Assert.NotNull(await new IPop.Infrastructure.Dashboard.DashboardService(db, permissions: new FeedPermissions(), shared: cache).GetAsync(me.Id));
        var first = counter.Texts.ToList();
        counter.Texts.Clear();
        await using (var db = new IPop.Infrastructure.Persistence.IPopDbContext(options))
            Assert.NotNull(await new IPop.Infrastructure.Dashboard.DashboardService(db, permissions: new FeedPermissions(), shared: cache).GetAsync(me.Id));

        // Roles, department and project memberships used to be read twice per Dashboard (once more by the calendar's news part).
        Assert.Single(first, q => q.Contains("\"UserRoles\"") && q.Contains("\"Name\"") && !q.Contains("EXISTS"));
        // The standalone membership read; the projects/milestones queries also name the table in subqueries.
        Assert.Single(first, q => q.TrimStart().StartsWith("SELECT \"p\".\"ProjectId\"") && q.Contains("FROM \"ProjectMembers\" AS \"p\"\nWHERE"));
        Assert.Single(first, q => q.Contains("FROM \"CallHistories\""));
        Assert.DoesNotContain(first, q => q.Contains("ROW_NUMBER") && q.Contains("\"CallParticipants\""));
        Assert.Single(first, q => q.Contains("FROM \"MailRecipients\""));
        Assert.Contains(first, q => q.Contains("\"EmailOutbox\""));
        Assert.DoesNotContain(counter.Texts, q => q.Contains("\"EmailOutbox\"") || q.Contains("\"HrisSyncRuns\""));
        // Permission checks are stubbed here (production caches them); the rest is one read per Dashboard part.
        Assert.True(first.Count <= 30, $"{first.Count} commands");
    }

    private sealed class CommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object>> ScalarExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
