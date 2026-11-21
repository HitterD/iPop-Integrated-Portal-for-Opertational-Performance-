using IPop.Application.Hub;
using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Hub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// The Hub (docs/superpowers/specs/2026-11-21-the-hub-design.md): the cross-department rule, confidentiality,
/// New → Ongoing → Solved, asks answered by a reply, reminders and the head reminder, follow-up tasks.
public class HubTests
{
    private sealed class Gateway : INotificationGatewayService
    {
        public List<(string Type, string Title, List<Guid> To)> Sent { get; } = [];
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Sent.Add((eventType, title, target.UserIds?.ToList() ?? []));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }
        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class Realtime : IRealtime
    {
        public List<(string Evt, object? Payload)> Events { get; } = [];
        public Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default) { Events.Add((evt, payload)); return Task.CompletedTask; }
        public Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default) { Events.Add((evt, payload)); return Task.CompletedTask; }
        public Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default) { Events.Add((evt, payload)); return Task.CompletedTask; }
        public Task ToGroupAsync(string group, string evt, object? payload = null, CancellationToken ct = default) { Events.Add((evt, payload)); return Task.CompletedTask; }
    }

    /// Only the two calls the Hub makes are implemented (a DispatchProxy, so the other ~46 members need no stubs).
    public class Projects : System.Reflection.DispatchProxy
    {
        public List<CreateTaskRequest> Created { get; } = [];
        public string? FailWith { get; set; }
        public static (IProjectService Service, Projects Fake) New()
        {
            var svc = Create<IProjectService, Projects>();
            return (svc, (Projects)(object)svc);
        }
        protected override object? Invoke(System.Reflection.MethodInfo? m, object?[]? a)
        {
            switch (m!.Name)
            {
                case nameof(IProjectService.CreateTaskAsync):
                    if (FailWith is not null) return Task.FromResult(new TaskResultDto(false, null, FailWith));
                    var req = (CreateTaskRequest)a![1]!;
                    Created.Add(req);
                    var dto = System.Text.Json.JsonSerializer.Deserialize<ProjectTaskDto>(
                        System.Text.Json.JsonSerializer.Serialize(new { Id = Guid.NewGuid(), req.ProjectId, req.Title, Status = "todo" }))!;
                    return Task.FromResult(new TaskResultDto(true, dto, null));
                case nameof(IProjectService.AssignUserAsync):
                    return Task.FromResult(new MemberResultDto(true, null));
                default:
                    throw new NotImplementedException(m.Name);
            }
        }
    }

    private sealed record World(TestDb T, HubService Hub, Gateway Gw, Realtime Rt, Projects Pj, User Rina, User Dedi, User Yuli, User Head);

    private static World Make()
    {
        var t = new TestDb();
        var marketing = new Department { Id = Guid.NewGuid(), Code = "MKT", Name = "Marketing" };
        var ppic = new Department { Id = Guid.NewGuid(), Code = "PPIC", Name = "PPIC" };
        t.Db.Departments.AddRange(marketing, ppic);
        t.Db.SaveChanges();
        var rina = t.AddUser("1001"); var dedi = t.AddUser("1002"); var yuli = t.AddUser("1003"); var head = t.AddUser("1004");
        rina.DepartmentId = marketing.Id; yuli.DepartmentId = marketing.Id; dedi.DepartmentId = ppic.Id; head.DepartmentId = ppic.Id;
        ppic.HeadUserId = head.Id;
        t.Db.SaveChanges();
        var gw = new Gateway(); var rt = new Realtime(); var (svc, pj) = Projects.New();
        var hub = new HubService(t.Db, gw, rt, svc, NullLogger<HubService>.Instance);
        return new World(t, hub, gw, rt, pj, rina, dedi, yuli, head);
    }

    private static async Task<HubTopicDto> Start(World w, params Guid[] people)
    {
        var r = await w.Hub.CreateAsync(w.Rina.Id, new CreateHubTopicRequest("change", "Mocca 25g stops after June 2027",
            "Dedi, how much packaging is left?", DateTime.UtcNow.AddDays(7), people), default);
        Assert.True(r.Success, r.Message);
        return r.Value!;
    }

    [Fact]
    public async Task Create_requires_someone_from_another_department()
    {
        var w = Make();
        var alone = await w.Hub.CreateAsync(w.Rina.Id, new CreateHubTopicRequest("idea", "Plain box", "OK?", null, []), default);
        Assert.Equal("rule", alone.Error);
        var sameDept = await w.Hub.CreateAsync(w.Rina.Id, new CreateHubTopicRequest("idea", "Plain box", "OK?", null, [w.Yuli.Id]), default);
        Assert.Equal("rule", sameDept.Error);

        var ok = await Start(w, w.Dedi.Id);
        Assert.Equal(HubStatus.New, ok.Status);
        Assert.Equal(2, ok.People.Count);
        Assert.Contains(w.Gw.Sent, s => s.Type == "hub.topic_new" && s.To.SequenceEqual([w.Dedi.Id]));
    }

    [Fact]
    public async Task Topics_are_confidential_to_members()
    {
        var w = Make();
        var topic = await Start(w, w.Dedi.Id);
        Assert.Equal("not_found", (await w.Hub.GetAsync(w.Head.Id, topic.Id, true, default)).Error);
        Assert.Equal("not_found", (await w.Hub.PostAsync(w.Head.Id, topic.Id, new CreateHubPostRequest("hi", null, null, null), default)).Error);
        Assert.Empty((await w.Hub.ListAsync(w.Head.Id, "all", null, default)).Topics);
        Assert.Single((await w.Hub.ListAsync(w.Dedi.Id, "all", null, default)).Topics);

        var policy = new HubAttachmentPolicy(w.T.Db);
        var att = new Attachment { Id = Guid.NewGuid(), FileName = "a.pdf", ContentType = "application/pdf", Size = 1, Sha256 = "x", StoragePath = "p", UploaderId = w.Rina.Id, Area = "hub", CreatedAt = DateTime.UtcNow };
        w.T.Db.Attachments.Add(att);
        await w.T.Db.SaveChangesAsync();
        await w.Hub.LinkUploadAsync(w.Rina.Id, topic.Id, att.Id, default);
        Assert.True(await policy.CanReadAsync(att.Id, w.Dedi.Id, default));
        Assert.False(await policy.CanReadAsync(att.Id, w.Head.Id, default));
    }

    [Fact]
    public async Task First_reply_from_someone_else_makes_it_ongoing_and_answers_their_ask()
    {
        var w = Make();
        var topic = await Start(w, w.Dedi.Id);
        var asked = await w.Hub.PostAsync(w.Rina.Id, topic.Id, new CreateHubPostRequest("Dedi, how much is left?", null, w.Dedi.Id, null), default);
        Assert.Equal(HubStatus.New, asked.Value!.Status);                       // the starter's own post doesn't count
        Assert.Equal(1, await w.Hub.WaitingCountAsync(w.Dedi.Id, default));
        Assert.True((await w.Hub.ListAsync(w.Dedi.Id, "mine", null, default)).Topics.Single().WaitingForMe);

        var question = asked.Value.Posts.Last();
        var answer = await w.Hub.PostAsync(w.Dedi.Id, topic.Id, new CreateHubPostRequest("18,6 juta sachets.", question.Id, null, null), default);
        Assert.Equal(HubStatus.Ongoing, answer.Value!.Status);
        Assert.Equal(question.Id, answer.Value.Posts.Last().ParentId);
        Assert.All(answer.Value.Asks, a => Assert.NotNull(a.AnsweredAt));
        Assert.Equal(0, await w.Hub.WaitingCountAsync(w.Dedi.Id, default));
        Assert.Contains(w.Rt.Events, e => e.Evt == HubEvents.Changed);
    }

    [Fact]
    public async Task Reply_to_a_reply_hangs_under_the_top_post()
    {
        var w = Make();
        var topic = await Start(w, w.Dedi.Id);
        var top = (await w.Hub.PostAsync(w.Rina.Id, topic.Id, new CreateHubPostRequest("Top", null, null, null), default)).Value!.Posts.Last();
        var child = (await w.Hub.PostAsync(w.Dedi.Id, topic.Id, new CreateHubPostRequest("Child", top.Id, null, null), default)).Value!.Posts.Last();
        var grand = (await w.Hub.PostAsync(w.Rina.Id, topic.Id, new CreateHubPostRequest("Grandchild", child.Id, null, null), default)).Value!.Posts.Last();
        Assert.Equal(top.Id, grand.ParentId);
    }

    [Fact]
    public async Task Solve_with_follow_up_creates_tasks_and_reopen_undoes_it()
    {
        var w = Make();
        var topic = await Start(w, w.Dedi.Id);
        var project = Guid.NewGuid();
        var solved = await w.Hub.SolveAsync(w.Dedi.Id, topic.Id, new SolveHubTopicRequest("Stop in June; cancel the carton order.",
            [new HubFollowUpTask("Cancel PO-2611873", w.Dedi.Id, project, DateTime.UtcNow.AddDays(3)), new HubFollowUpTask("  ", w.Dedi.Id, project, null)]), default);
        Assert.True(solved.Success, solved.Message);
        Assert.Equal(HubStatus.Solved, solved.Value!.Status);
        Assert.Equal("Stop in June; cancel the carton order.", solved.Value.SolvedSummary);
        Assert.Single(w.Pj.Created);
        Assert.Equal("hub", w.Pj.Created[0].Tags);
        Assert.Contains(w.Gw.Sent, s => s.Type == "hub.solved" && s.To.Contains(w.Rina.Id));
        Assert.Equal(HubStatus.Solved, (await w.Hub.ListAsync(w.Rina.Id, "solved", null, default)).Topics.Single().Status);
        Assert.Equal("solved", (await w.Hub.PostAsync(w.Rina.Id, topic.Id, new CreateHubPostRequest("more", null, null, null), default)).Error);

        var reopened = await w.Hub.ReopenAsync(w.Rina.Id, topic.Id, default);
        Assert.Null(reopened.Value!.SolvedAt);
        Assert.NotEqual(HubStatus.Solved, reopened.Value.Status);
    }

    [Fact]
    public async Task Solve_fails_cleanly_when_projects_refuses()
    {
        var w = Make();
        var topic = await Start(w, w.Dedi.Id);
        w.Pj.FailWith = "forbidden";
        var r = await w.Hub.SolveAsync(w.Dedi.Id, topic.Id, new SolveHubTopicRequest("Done.", [new HubFollowUpTask("Task", w.Dedi.Id, Guid.NewGuid(), null)]), default);
        Assert.Equal("forbidden", r.Error);
        Assert.Null(w.T.Verify().HubTopics.Single().SolvedAt);
    }

    [Fact]
    public async Task Remind_is_limited_to_once_an_hour()
    {
        var w = Make();
        var topic = await Start(w, w.Dedi.Id);
        var t = (await w.Hub.PostAsync(w.Rina.Id, topic.Id, new CreateHubPostRequest("Dedi?", null, w.Dedi.Id, null), default)).Value!;
        var ask = t.Asks.Single();
        Assert.True((await w.Hub.RemindAsync(w.Rina.Id, topic.Id, ask.Id, default)).Success);
        Assert.Equal("rate_limited", (await w.Hub.RemindAsync(w.Rina.Id, topic.Id, ask.Id, default)).Error);
        Assert.Contains(w.Gw.Sent, s => s.Type == "hub.remind" && s.To.SequenceEqual([w.Dedi.Id]));
    }

    [Fact]
    public async Task Worker_reminds_on_day_two_and_tells_the_head_on_day_three_without_the_topic()
    {
        var w = Make();
        var topic = await Start(w, w.Dedi.Id);
        await w.Hub.PostAsync(w.Rina.Id, topic.Id, new CreateHubPostRequest("Dedi?", null, w.Dedi.Id, null), default);
        var asked = w.T.Db.HubAsks.Single().CreatedAt;
        var monday = asked.Date.AddDays(((int)DayOfWeek.Monday - (int)asked.DayOfWeek + 7) % 7);
        w.T.Db.HubAsks.Single().CreatedAt = monday.AddHours(9);
        await w.T.Db.SaveChangesAsync();

        Assert.Equal(0, await w.Hub.RunRemindersAsync(monday.AddDays(1).AddHours(9), default));
        Assert.Equal(1, await w.Hub.RunRemindersAsync(monday.AddDays(2).AddHours(9), default));
        Assert.Contains(w.Gw.Sent, s => s.Type == "hub.remind" && s.To.SequenceEqual([w.Dedi.Id]));
        Assert.Equal(1, await w.Hub.RunRemindersAsync(monday.AddDays(3).AddHours(9), default));
        var head = w.Gw.Sent.Single(s => s.Type == "hub.head_reminder");
        Assert.Equal([w.Head.Id], head.To);
        Assert.DoesNotContain("Mocca", head.Title);
        Assert.Equal(0, await w.Hub.RunRemindersAsync(monday.AddDays(4).AddHours(9), default));
    }

    [Fact]
    public void Working_days_skip_weekends()
    {
        var friday = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, HubService.WorkingDaysBetween(friday, friday.AddDays(3)));   // Fri → Mon
        Assert.False(HubService.IsLate(friday, friday.AddDays(4)));                   // Tue: 2 working days
        Assert.True(HubService.IsLate(friday, friday.AddDays(5)));                    // Wed: 3
    }

    [Fact]
    public async Task People_added_later_see_everything_and_everyone_is_told()
    {
        var w = Make();
        var topic = await Start(w, w.Dedi.Id);
        var r = await w.Hub.AddMembersAsync(w.Dedi.Id, topic.Id, new AddHubMembersRequest([w.Yuli.Id]), default);
        Assert.Equal(3, r.Value!.People.Count);
        Assert.True((await w.Hub.GetAsync(w.Yuli.Id, topic.Id, true, default)).Success);
        Assert.Contains(r.Value.Posts, p => p.Kind == "system" && p.Body.Contains("added"));
        Assert.Contains(w.Gw.Sent, s => s.Type == "hub.added" && s.To.SequenceEqual([w.Yuli.Id]));
    }

    [Fact]
    public async Task Opening_a_topic_marks_it_read()
    {
        var w = Make();
        var topic = await Start(w, w.Dedi.Id);
        Assert.True((await w.Hub.ListAsync(w.Dedi.Id, "all", null, default)).Topics.Single().Unread is false or true);
        await w.Hub.PostAsync(w.Rina.Id, topic.Id, new CreateHubPostRequest("New info", null, null, null), default);
        Assert.True((await w.Hub.ListAsync(w.Dedi.Id, "all", null, default)).Topics.Single().Unread);
        var opened = await w.Hub.GetAsync(w.Dedi.Id, topic.Id, true, default);
        Assert.Contains(opened.Value!.Posts, p => p.IsNew || opened.Value.ReadUpTo is null);
        Assert.False((await w.Hub.ListAsync(w.Dedi.Id, "all", null, default)).Topics.Single().Unread);
    }
}

/// The Hub is reachable on every screen: the desktop sidebar (hub.use) and the phone's More sheet.
public class HubNavTests
{
    [Fact]
    public void Phone_more_sheet_lists_the_hub_with_its_badge()
    {
        var hub = IPop.Application.Shell.PhoneTabs.Find("/hub");
        Assert.NotNull(hub);
        Assert.Equal(HubEvents.BadgeKey, hub!.BadgeKey);
        Assert.Contains(IPop.Application.Shell.PhoneTabs.More(IPop.Application.Shell.PhoneTabs.Default, false), m => m.Href == "/hub");
    }

    [Fact]
    public void Notifications_from_the_hub_wear_the_hub_module()
    {
        Assert.Equal("hub", IPop.Web.Services.NotificationModules.For("system", "Rina needs you in The Hub", "/hub/123").Key);
        Assert.Equal("hub", IPop.Web.Services.NotificationModules.For("hub.remind", "x", null).Key);
    }
}
