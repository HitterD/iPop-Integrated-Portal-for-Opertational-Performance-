using IPop.Core.Entities;
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
            Id = Guid.NewGuid(), ProjectId = project.Id, Title = "Calibrate roaster", Status = "todo",
            DueDate = DateTime.UtcNow.Date.AddDays(-2), CreatedById = me.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var done = new ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Title = "Order beans", Status = "done",
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

        var start = DateTime.UtcNow.AddMinutes(30);
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
        Assert.Equal("Rina", dto.FirstName);
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
}
