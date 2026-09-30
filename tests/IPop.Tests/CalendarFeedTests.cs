using Xunit;
using IPop.Api.Endpoints;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Microsoft.EntityFrameworkCore;
using static IPop.Api.Endpoints.CalendarEndpoints;

namespace IPop.Tests;

/// Unified calendar: every source is scoped to what the caller may see, and only
/// the owner can change an event.
public class CalendarFeedTests
{
    private static readonly DateTime From = DateTime.UtcNow.Date.AddDays(-3);
    private static readonly DateTime To = DateTime.UtcNow.Date.AddDays(30);
    private static DateTime Day(int offset) => DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(offset), DateTimeKind.Utc);

    private static ProjectTask Task(TestDb t, Guid projectId, string title, string status, DateTime? due, params Guid[] assignees)
    {
        var task = new ProjectTask { Id = Guid.NewGuid(), ProjectId = projectId, GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == projectId).Id, Title = title, Status = status, DueDate = due, CreatedById = t.Db.Projects.Find(projectId)!.OwnerId };
        t.Db.ProjectTasks.Add(task);
        foreach (var a in assignees) t.Db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = task.Id, UserId = a, AssignedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        return task;
    }

    [Fact]
    public async Task Tasks_only_mine_not_done_in_range()
    {
        using var t = new TestDb();
        var me = t.AddUser("100"); var other = t.AddUser("200");
        var p = t.AddProject("Alpha", me.Id);
        Task(t, p.Id, "Mine", "todo", Day(2), me.Id);
        Task(t, p.Id, "Shared", "doing", Day(3), me.Id, other.Id);
        Task(t, p.Id, "Others", "todo", Day(2), other.Id);
        Task(t, p.Id, "Done", "done", Day(2), me.Id);
        Task(t, p.Id, "Far", "todo", Day(90), me.Id);
        Task(t, p.Id, "Undated", "todo", null, me.Id);

        var feed = await BuildFeedAsync(t.Db, me.Id, From, To, FeedAccess.All, default);
        var tasks = feed.Where(i => i.Id.StartsWith("task:")).Select(i => i.Title).OrderBy(x => x).ToList();
        Assert.Equal(["Mine", "Shared"], tasks);
        var mine = feed.First(i => i.Title == "Mine");
        Assert.True(mine.AllDay);
        Assert.StartsWith($"/projects/{p.Id}?task=", mine.Url);
    }

    [Fact]
    public async Task Milestones_and_holidays_only_from_my_projects()
    {
        using var t = new TestDb();
        var me = t.AddUser("100"); var other = t.AddUser("200");
        var mine = t.AddProject("Mine", me.Id);
        var theirs = t.AddProject("Theirs", other.Id);
        t.Db.ProjectMilestones.Add(new ProjectMilestone { Id = Guid.NewGuid(), ProjectId = mine.Id, Title = "Go-live", DueDate = Day(5), CreatedAt = DateTime.UtcNow });
        t.Db.ProjectMilestones.Add(new ProjectMilestone { Id = Guid.NewGuid(), ProjectId = theirs.Id, Title = "Secret", DueDate = Day(5), CreatedAt = DateTime.UtcNow });
        t.Db.WorkCalendarEntries.Add(new WorkCalendarEntry { Id = Guid.NewGuid(), ProjectId = mine.Id, Date = Day(6), Kind = "holiday", Title = "Libur", CreatedById = me.Id });
        t.Db.WorkCalendarEntries.Add(new WorkCalendarEntry { Id = Guid.NewGuid(), ProjectId = mine.Id, Date = Day(7), Kind = "workday", Title = "Lembur", CreatedById = me.Id });
        t.Db.WorkCalendarEntries.Add(new WorkCalendarEntry { Id = Guid.NewGuid(), ProjectId = theirs.Id, Date = Day(6), Kind = "holiday", Title = "Hidden", CreatedById = other.Id });
        t.Db.SaveChanges();

        var titles = (await BuildFeedAsync(t.Db, me.Id, From, To, FeedAccess.All, default)).Select(i => i.Title).ToList();
        Assert.Contains("Go-live", titles);
        Assert.Contains("Libur", titles);
        Assert.DoesNotContain("Secret", titles);
        Assert.DoesNotContain("Hidden", titles);
        Assert.DoesNotContain("Lembur", titles);
    }

    [Fact]
    public async Task Sources_are_dropped_without_permission()
    {
        using var t = new TestDb();
        var me = t.AddUser("100");
        var p = t.AddProject("Alpha", me.Id);
        Task(t, p.Id, "Mine", "todo", Day(2), me.Id);
        t.Db.Mails.Add(new Mail { Id = Guid.NewGuid(), SenderId = me.Id, Subject = "Report", Body = "x", Status = "scheduled", ScheduledAt = Day(4).AddHours(9), CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        var none = await BuildFeedAsync(t.Db, me.Id, From, To, new FeedAccess(false, false, false, false), default);
        Assert.Empty(none);
        var all = await BuildFeedAsync(t.Db, me.Id, From, To, FeedAccess.All, default);
        Assert.Contains(all, i => i.Source == "projects");
        Assert.Contains(all, i => i.Source == "mail" && i.Url!.StartsWith("/mail?mailId="));
    }

    [Fact]
    public async Task Scheduled_mail_only_mine_and_not_cancelled()
    {
        using var t = new TestDb();
        var me = t.AddUser("100"); var other = t.AddUser("200");
        t.Db.Mails.AddRange(
            new Mail { Id = Guid.NewGuid(), SenderId = me.Id, Subject = "Mine", Body = "x", Status = "scheduled", ScheduledAt = Day(2), CreatedAt = DateTime.UtcNow },
            new Mail { Id = Guid.NewGuid(), SenderId = me.Id, Subject = "Cancelled", Body = "x", Status = "scheduled", ScheduledAt = Day(2), CancelledAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow },
            new Mail { Id = Guid.NewGuid(), SenderId = me.Id, Subject = "Sent", Body = "x", Status = "sent", ScheduledAt = Day(2), CreatedAt = DateTime.UtcNow },
            new Mail { Id = Guid.NewGuid(), SenderId = other.Id, Subject = "Theirs", Body = "x", Status = "scheduled", ScheduledAt = Day(2), CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        var mails = (await BuildFeedAsync(t.Db, me.Id, From, To, FeedAccess.All, default)).Where(i => i.Source == "mail").Select(i => i.Title).ToList();
        Assert.Equal(["Mine"], mails);
    }

    [Fact]
    public async Task News_respects_targets_and_future_posts_need_manager()
    {
        using var t = new TestDb();
        var me = t.AddUser("100"); var author = t.AddHr("900");
        NewsPost Post(string title, DateTime? publishAt, string? targetNik = null)
        {
            var p = new NewsPost
            {
                Id = Guid.NewGuid(), Type = "memo", Title = title, Body = "b", Status = "published", AuthorId = author.Id,
                PublishAt = publishAt, PublishedAt = publishAt is null ? Day(1) : null, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            t.Db.NewsPosts.Add(p);
            if (targetNik is not null) t.Db.NewsTargets.Add(new NewsTarget { Id = Guid.NewGuid(), NewsPostId = p.Id, Kind = "user", Value = targetNik });
            return p;
        }
        Post("Everyone", null);
        Post("ForMe", null, "100");
        Post("ForOther", null, "555");
        Post("Upcoming", Day(10));
        t.Db.SaveChanges();

        var reader = (await BuildFeedAsync(t.Db, me.Id, From, To, FeedAccess.All, default)).Where(i => i.Source == "news").Select(i => i.Title).OrderBy(x => x).ToList();
        Assert.Equal(["Everyone", "ForMe"], reader);
        var manager = (await BuildFeedAsync(t.Db, author.Id, From, To, FeedAccess.All, default)).Where(i => i.Source == "news").Select(i => i.Title).ToList();
        Assert.Contains("Upcoming", manager);
    }

    [Fact]
    public async Task Meetings_show_for_attendees_but_not_outsiders_or_when_cancelled()
    {
        using var t = new TestDb();
        var me = t.AddUser("100"); var guest = t.AddUser("200"); var outsider = t.AddUser("300");
        var (r, ev, invited) = await CreateEventAsync(t.Db, me.Id,
            new SaveEventRequest("Sync", Day(2).AddHours(3), Day(2).AddHours(4), AttendeeIds: [guest.Id]), default);
        Assert.Equal(SaveResult.Ok, r);
        Assert.NotNull(ev!.MeetingId);
        Assert.Equal([guest.Id], invited);

        var guestFeed = await BuildFeedAsync(t.Db, guest.Id, From, To, FeedAccess.All, default);
        var item = Assert.Single(guestFeed, i => i.Title == "Sync");
        Assert.Equal("pending", item.Rsvp);
        Assert.False(item.CanEdit);
        Assert.DoesNotContain(await BuildFeedAsync(t.Db, outsider.Id, From, To, FeedAccess.All, default), i => i.Title == "Sync");
        Assert.True(Assert.Single(await BuildFeedAsync(t.Db, me.Id, From, To, FeedAccess.All, default), i => i.Title == "Sync").CanEdit);

        var (del, _, attendees) = await DeleteEventAsync(t.Db, ev.Id, me.Id, default);
        Assert.Equal(SaveResult.Ok, del);
        Assert.Equal([guest.Id], attendees);
        Assert.DoesNotContain(await BuildFeedAsync(t.Db, guest.Id, From, To, FeedAccess.All, default), i => i.Title == "Sync");
    }

    [Fact]
    public async Task Room_event_requires_membership_and_chat_permission()
    {
        using var t = new TestDb();
        var me = t.AddUser("100"); var mate = t.AddUser("200"); var outsider = t.AddUser("300");
        var room = t.AddRoom("group", "Ops");
        t.AddMember(room.Id, me.Id, "owner"); t.AddMember(room.Id, mate.Id);

        var (denied, _, _) = await CreateEventAsync(t.Db, outsider.Id, new SaveEventRequest("X", Day(1), Day(1).AddHours(1), RoomId: room.Id), default);
        Assert.Equal(SaveResult.NotRoomMember, denied);

        var (ok, _, invited) = await CreateEventAsync(t.Db, me.Id, new SaveEventRequest("Standup", Day(1).AddHours(2), Day(1).AddHours(3), RoomId: room.Id), default);
        Assert.Equal(SaveResult.Ok, ok);
        Assert.Equal([mate.Id], invited);
        var item = Assert.Single(await BuildFeedAsync(t.Db, mate.Id, From, To, FeedAccess.All, default), i => i.Title == "Standup");
        Assert.Equal("chat", item.Source);
        Assert.Equal($"/chat/{room.Id}", item.Url);
        Assert.DoesNotContain(await BuildFeedAsync(t.Db, mate.Id, From, To, FeedAccess.All with { Chat = false }, default), i => i.Title == "Standup");
    }

    [Fact]
    public async Task Personal_event_crud_is_owner_only()
    {
        using var t = new TestDb();
        var me = t.AddUser("100"); var other = t.AddUser("200");
        var (r, ev, _) = await CreateEventAsync(t.Db, me.Id,
            new SaveEventRequest("Dentist", Day(3), Day(3), AllDay: true, Location: "Klinik", ReminderMinutes: 60), default);
        Assert.Equal(SaveResult.Ok, r);
        Assert.Null(ev!.MeetingId);
        Assert.Equal(Day(4), ev.EndAt); // all-day end is exclusive, at least one day

        var hidden = await BuildFeedAsync(t.Db, other.Id, From, To, FeedAccess.All, default);
        Assert.DoesNotContain(hidden, i => i.Title == "Dentist");
        Assert.Null(await LoadDetailAsync(t.Db, ev.Id, other.Id, default));

        var upd = new SaveEventRequest("Hacked", Day(3), Day(3), AllDay: true);
        Assert.Equal(SaveResult.Forbidden, (await UpdateEventAsync(t.Db, ev.Id, other.Id, upd, default)).Item1);
        Assert.Equal(SaveResult.Forbidden, (await DeleteEventAsync(t.Db, ev.Id, other.Id, default)).Item1);

        Assert.Equal(SaveResult.Ok, (await UpdateEventAsync(t.Db, ev.Id, me.Id, upd with { Title = "Dentist 2" }, default)).Item1);
        Assert.Equal("Dentist 2", (await t.Verify().CalendarEvents.SingleAsync()).Title);
        Assert.Equal(SaveResult.Ok, (await DeleteEventAsync(t.Db, ev.Id, me.Id, default)).Item1);
        Assert.Empty(t.Verify().CalendarEvents);
    }

    [Fact]
    public async Task Invalid_input_is_rejected()
    {
        using var t = new TestDb();
        var me = t.AddUser("100");
        Assert.Equal(SaveResult.InvalidTitle, (await CreateEventAsync(t.Db, me.Id, new SaveEventRequest(" ", Day(1), Day(1).AddHours(1)), default)).Item1);
        Assert.Equal(SaveResult.InvalidRange, (await CreateEventAsync(t.Db, me.Id, new SaveEventRequest("X", Day(1).AddHours(2), Day(1).AddHours(1)), default)).Item1);
    }

    // ── Group photo ──

    [Theory]
    [InlineData("a.png", 1000, null)]
    [InlineData("a.JPG", 1000, null)]
    [InlineData("a.pdf", 1000, "not_an_image")]
    [InlineData("a.png", 3 * 1024 * 1024, "too_large")]
    [InlineData(null, 0, "no_file")]
    public void Group_avatar_validation(string? name, long size, string? expected)
        => Assert.Equal(expected, ChatEndpoints.ValidateGroupAvatar(name, size));

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/api/chat-avatars/../secret.jpg")]
    [InlineData("abc.jpg")]
    [InlineData(null)]
    public void Group_avatar_path_rejects_traversal(string? input)
        => Assert.Null(ChatEndpoints.GroupAvatarPath("C:/root", input));

    [Fact]
    public void Group_avatar_path_accepts_generated_names()
    {
        var name = $"{Guid.NewGuid():N}.jpg";
        Assert.EndsWith(name, ChatEndpoints.GroupAvatarPath("C:/root", $"/api/chat-avatars/{name}"));
    }

    [Fact]
    public async Task Only_owner_or_admin_can_set_group_photo()
    {
        using var t = new TestDb();
        var owner = t.AddUser("100"); var member = t.AddUser("200");
        var room = t.AddRoom("group", "Ops");
        t.AddMember(room.Id, owner.Id, "owner"); t.AddMember(room.Id, member.Id);
        var dm = t.AddRoom("dm"); t.AddMember(dm.Id, owner.Id);
        IPop.Application.Chat.IChatService svc = new ChatService(t.Db);

        Assert.Equal("forbidden", (await svc.SetGroupAvatarAsync(member.Id, room.Id, "/api/chat-avatars/x.jpg", default)).Error);
        Assert.Equal("not_a_group", (await svc.SetGroupAvatarAsync(owner.Id, dm.Id, "/api/chat-avatars/x.jpg", default)).Error);

        var first = await svc.SetGroupAvatarAsync(owner.Id, room.Id, "/api/chat-avatars/a.jpg", default);
        Assert.True(first.Success);
        Assert.Null(first.Error);
        var second = await svc.SetGroupAvatarAsync(owner.Id, room.Id, null, default);
        Assert.True(second.Success);
        Assert.Equal("/api/chat-avatars/a.jpg", second.Error); // previous URL, so the endpoint can delete the file
        Assert.Null((await t.Verify().ChatRooms.FindAsync(room.Id))!.AvatarUrl);
    }
}
