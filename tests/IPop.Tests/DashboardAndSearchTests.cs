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
}
