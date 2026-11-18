using System.Text.Json;
using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Projects v2 stage 4: My work, request form, close, automations, overview, weekly workload, portfolio fields.
public sealed class ProjectStage4Tests
{
    private sealed class Recorder : INotificationGatewayService
    {
        public List<(string Event, Guid? User)> Sent { get; } = [];
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Sent.Add((eventType, target.UserId));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }
        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class Env : IDisposable
    {
        public TestDb T = new();
        public Recorder Gw = new();
        public User Owner, Member, Viewer, Outsider;
        public Guid P;
        public Env()
        {
            Owner = T.AddUser("S4_O"); Member = T.AddUser("S4_M"); Viewer = T.AddUser("S4_V"); Outsider = T.AddUser("S4_X");
            var projects = Projects();
            P = projects.CreateProjectAsync(Owner.Id, new CreateProjectRequest("S4", null, 2026, Owner.Id, null, null, null, null), default).Result.Project!.Id;
            T.AddProjectMember(P, Member.Id); T.AddProjectMember(P, Viewer.Id, "viewer");
        }
        public ProjectService Projects() => new(T.Verify(), Gw);
        public async Task<Guid> TaskAsync(string title, string status = "todo", DateTime? start = null, DateTime? due = null, decimal? hours = null, params Guid[] assignees)
        {
            var db = T.Db;
            await new ProjectStatusService(db).EnsureStatusesAsync(P, default);
            var group = db.ProjectGroups.First(g => g.ProjectId == P);
            var opt = db.ProjectStatusOptions.Where(o => o.ProjectId == P && o.Stage == status).OrderBy(o => o.SortOrder).First();
            var t = new ProjectTask { Id = Guid.NewGuid(), ProjectId = P, GroupId = group.Id, Title = title, Status = status, StatusOptionId = opt.Id,
                StartDate = start, DueDate = due, EstimatedHours = hours, CreatedById = Owner.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                CompletedAt = status == "done" ? DateTime.UtcNow : null };
            db.ProjectTasks.Add(t);
            foreach (var a in assignees) db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = t.Id, UserId = a, AssignedAt = DateTime.UtcNow });
            db.SaveChanges();
            return t.Id;
        }
        public void Dispose() => T.Dispose();
    }

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);
    // 2026-11-18 is a Wednesday; its week is Mon 16 Nov - Sun 22 Nov.
    private static readonly DateTime Today = D(2026, 11, 18);

    // ───────────── My work ─────────────

    [Fact]
    public async Task MyWork_buckets_by_local_week_and_counts_done_this_week()
    {
        using var e = new Env();
        await e.TaskAsync("late", due: D(2026, 11, 17), assignees: e.Member.Id);
        await e.TaskAsync("today", due: Today, assignees: e.Member.Id);
        await e.TaskAsync("sunday", due: D(2026, 11, 22), assignees: e.Member.Id);
        await e.TaskAsync("nextmon", due: D(2026, 11, 23), assignees: e.Member.Id);
        await e.TaskAsync("nodue", assignees: e.Member.Id);
        await e.TaskAsync("someone else", due: Today, assignees: e.Owner.Id);
        var doneId = await e.TaskAsync("finished", "done", assignees: e.Member.Id);
        var db = e.T.Db; var done = db.ProjectTasks.Find(doneId)!; done.CompletedAt = D(2026, 11, 16); db.SaveChanges();

        var r = await new MyWorkService(e.T.Verify()).GetAsync(e.Member.Id, Today, default);
        Assert.Equal(new MyWorkCountsDto(1, 1, 1, 2, 1), r.Counts);
        Assert.Equal(["late", "today", "sunday", "nextmon", "nodue"], r.Items.Select(i => i.Title));
        Assert.True(r.Items[0].IsLate);
        Assert.Equal("Not started", r.Items[1].Status);
        Assert.Equal("Tasks", r.Items[1].GroupName);
    }

    [Fact]
    public async Task MyWork_ignores_completed_projects_and_waiting_blocked_and_request()
    {
        using var e = new Env();
        var mine = await e.TaskAsync("mine", due: Today, assignees: e.Member.Id);
        var theirs = await e.TaskAsync("theirs", assignees: e.Owner.Id);
        e.T.Db.ProjectTaskDependencies.Add(new ProjectTaskDependency { TaskId = theirs, DependsOnTaskId = mine });
        e.T.Db.SaveChanges();
        var form = new ProjectFormService(e.T.Verify(), e.Gw);
        await form.SaveAsync(e.P, e.Owner.Id, new SaveProjectFormRequest(true, "Ask", "", "project", e.Member.Id,
            [new("t", "Title", "text", true, null, "title")]), default);
        await form.SubmitAsync(e.P, e.Owner.Id, new SubmitFormRequest(new() { ["t"] = "Need laptop" }), default);

        var r = await new MyWorkService(e.T.Verify()).GetAsync(e.Member.Id, Today, default);
        Assert.Contains(r.WaitingOnMe, w => w.Kind == "blocked" && w.Text.Contains("1 task waits for mine"));
        Assert.Contains(r.WaitingOnMe, w => w.Kind == "request" && w.FromName == "User S4_O");

        var p = e.T.Db.Projects.Find(e.P)!; p.Status = "completed"; e.T.Db.SaveChanges();
        var after = await new MyWorkService(e.T.Verify()).GetAsync(e.Member.Id, Today, default);
        Assert.Empty(after.Items);
    }

    // ───────────── Request form ─────────────

    private static SaveProjectFormRequest Form(bool open = true, string scope = "project", Guid? assignee = null, params FormQuestionDto[] qs) =>
        new(open, "Request", "Tell us", scope, assignee, qs.Length > 0 ? qs :
        [
            new("what", "What", "text", true, null, "title"),
            new("why", "Why", "longtext", false, null, "description"),
            new("when", "When", "date", false, null, "dueDate"),
            new("kind", "Kind", "choice", false, ["A", "B"], "none"),
        ]);

    [Fact]
    public async Task Form_get_creates_default_and_only_managers_edit()
    {
        using var e = new Env();
        var svc = new ProjectFormService(e.T.Verify(), e.Gw);
        var f = (await svc.GetAsync(e.P, e.Owner.Id, default)).Value!;
        Assert.False(f.IsOpen);
        Assert.Single(f.Questions, q => q.MapsTo == "title");
        Assert.Equal("forbidden", (await svc.GetAsync(e.P, e.Member.Id, default)).Error);
        Assert.Equal("forbidden", (await svc.SaveAsync(e.P, e.Viewer.Id, Form(), default)).Error);
        Assert.Equal("forbidden", (await svc.SaveAsync(e.P, e.Outsider.Id, Form(), default)).Error);
    }

    [Fact]
    public async Task Form_save_validates_and_creates_intake_group_when_opened()
    {
        using var e = new Env();
        var svc = new ProjectFormService(e.T.Verify(), e.Gw);
        Assert.Equal("title_mapping_required", (await svc.SaveAsync(e.P, e.Owner.Id, Form(true, "project", null, new FormQuestionDto("a", "A", "text", false, null, "none")), default)).Error);
        Assert.Equal("invalid_scope", (await svc.SaveAsync(e.P, e.Owner.Id, Form() with { AudienceScope = "world" }, default)).Error);
        Assert.Equal("invalid_type", (await svc.SaveAsync(e.P, e.Owner.Id, Form(true, "project", null, new FormQuestionDto("a", "A", "file", false, null, "title")), default)).Error);
        Assert.Equal("invalid_options", (await svc.SaveAsync(e.P, e.Owner.Id, Form(true, "project", null, new FormQuestionDto("a", "A", "choice", false, [], "title")), default)).Error);
        Assert.Equal("invalid_assignee", (await svc.SaveAsync(e.P, e.Owner.Id, Form(true, "project", e.Outsider.Id), default)).Error);
        var tooMany = Enumerable.Range(0, 13).Select(i => new FormQuestionDto("q" + i, "Q", "text", false, null, i == 0 ? "title" : "none")).ToArray();
        Assert.Equal("too_many_questions", (await svc.SaveAsync(e.P, e.Owner.Id, Form(true, "project", null, tooMany), default)).Error);

        var ok = (await svc.SaveAsync(e.P, e.Owner.Id, Form(), default)).Value!;
        Assert.NotNull(ok.IntakeGroupId);
        Assert.Equal("Intake", e.T.Verify().ProjectGroups.Single(g => g.Id == ok.IntakeGroupId).Name);
        await svc.SaveAsync(e.P, e.Owner.Id, Form(), default);
        Assert.Equal(1, e.T.Verify().ProjectGroups.Count(g => g.ProjectId == e.P && g.Name == "Intake"));
    }

    [Theory]
    [InlineData("project", false, false, false)]
    [InlineData("department", true, false, false)]
    [InlineData("everyone", true, true, false)]
    public async Task Form_audience_scopes(string scope, bool sameDeptOk, bool outsiderOk, bool _)
    {
        using var e = new Env();
        var dept = e.T.EnsureDepartment("D1", "Dept"); var other = e.T.EnsureDepartment("D2", "Other");
        e.T.AssignDepartment(e.Owner.Id, dept.Id);
        var colleague = e.T.AddUser("S4_C"); e.T.AssignDepartment(colleague.Id, dept.Id);
        e.T.AssignDepartment(e.Outsider.Id, other.Id);
        var svc = new ProjectFormService(e.T.Verify(), e.Gw);
        await svc.SaveAsync(e.P, e.Owner.Id, Form(true, scope), default);

        Assert.True((await svc.GetPublicAsync(e.P, e.Member.Id, default)).Success);          // members always
        Assert.Equal(sameDeptOk, (await svc.GetPublicAsync(e.P, colleague.Id, default)).Success);
        Assert.Equal(outsiderOk, (await svc.GetPublicAsync(e.P, e.Outsider.Id, default)).Success);
        var denied = outsiderOk ? null : (await svc.SubmitAsync(e.P, e.Outsider.Id, new SubmitFormRequest(new() { ["what"] = "x" }), default)).Error;
        Assert.Equal(outsiderOk ? null : "forbidden", denied);
    }

    [Fact]
    public async Task Form_closed_is_not_found()
    {
        using var e = new Env();
        var svc = new ProjectFormService(e.T.Verify(), e.Gw);
        await svc.SaveAsync(e.P, e.Owner.Id, Form(false), default);
        Assert.Equal("form_not_found", (await svc.GetPublicAsync(e.P, e.Member.Id, default)).Error);
        Assert.Equal("form_not_found", (await svc.SubmitAsync(e.P, e.Member.Id, new SubmitFormRequest(new() { ["what"] = "x" }), default)).Error);
    }

    [Fact]
    public async Task Form_submit_validates_answers()
    {
        using var e = new Env();
        var svc = new ProjectFormService(e.T.Verify(), e.Gw);
        await svc.SaveAsync(e.P, e.Owner.Id, Form(), default);
        Task<string?> Err(Dictionary<string, string> a) => Task.Run(async () => (await svc.SubmitAsync(e.P, e.Member.Id, new SubmitFormRequest(a), default)).Error);
        Assert.Equal("required_missing", await Err(new() { ["why"] = "because" }));
        Assert.Equal("invalid_choice", await Err(new() { ["what"] = "x", ["kind"] = "Z" }));
        Assert.Equal("invalid_date", await Err(new() { ["what"] = "x", ["when"] = "tomorrow" }));
        Assert.Equal("answer_too_long", await Err(new() { ["what"] = new string('a', 2001) }));
    }

    [Fact]
    public async Task Form_submit_creates_intake_task_notifies_assignee_once()
    {
        using var e = new Env();
        var svc = new ProjectFormService(e.T.Verify(), e.Gw);
        await svc.SaveAsync(e.P, e.Owner.Id, Form(true, "project", e.Owner.Id), default);
        var r = (await svc.SubmitAsync(e.P, e.Member.Id, new SubmitFormRequest(new()
            { ["what"] = new string('T', 250), ["why"] = "Because", ["when"] = "2026-12-01", ["kind"] = "B" }), default)).Value!;
        var db = e.T.Verify();
        var t = db.ProjectTasks.Include(x => x.Group).Single(x => x.Id == r.TaskId);
        Assert.Equal(200, t.Title.Length);
        Assert.Equal("Intake", t.Group.Name);
        Assert.Equal("todo", t.Status);
        Assert.NotNull(t.StatusOptionId);
        Assert.Equal(D(2026, 12, 1), t.DueDate);
        Assert.Contains("Because", t.Description);
        Assert.Contains("Kind: B", t.Description);
        Assert.Equal([e.Owner.Id], db.ProjectTaskAssignees.Where(a => a.TaskId == t.Id).Select(a => a.UserId).ToList());
        Assert.Single(db.ProjectFormSubmissions, s => s.TaskId == t.Id && s.SubmitterId == e.Member.Id);
        Assert.Single(db.ProjectTaskActivities, a => a.TaskId == t.Id && a.Action == "form_request");
        Assert.Single(e.Gw.Sent, s => s.Event == "project.form_request" && s.User == e.Owner.Id);
    }

    [Fact]
    public async Task Form_rate_limit_is_ten_per_hour_per_user()
    {
        using var e = new Env();
        var svc = new ProjectFormService(e.T.Verify(), e.Gw);
        await svc.SaveAsync(e.P, e.Owner.Id, Form(), default);
        for (var i = 0; i < 10; i++) Assert.True((await svc.SubmitAsync(e.P, e.Member.Id, new SubmitFormRequest(new() { ["what"] = "n" + i }), default)).Success);
        Assert.Equal("rate_limited", (await svc.SubmitAsync(e.P, e.Member.Id, new SubmitFormRequest(new() { ["what"] = "more" }), default)).Error);
        Assert.True((await svc.SubmitAsync(e.P, e.Owner.Id, new SubmitFormRequest(new() { ["what"] = "other user" }), default)).Success);
    }

    [Fact]
    public async Task Form_submitter_notified_on_stage_changes_only_for_submissions()
    {
        using var e = new Env();
        var form = new ProjectFormService(e.T.Verify(), e.Gw);
        await form.SaveAsync(e.P, e.Owner.Id, Form(), default);
        var tid = (await form.SubmitAsync(e.P, e.Member.Id, new SubmitFormRequest(new() { ["what"] = "Need" }), default)).Value!.TaskId;
        var plain = await e.TaskAsync("plain");
        e.Gw.Sent.Clear();

        async Task Move(Guid id, string stage) => await e.Projects().MoveTaskAsync(e.Owner.Id, id, new TaskMoveRequest(stage, null), default);
        await Move(tid, "doing");
        await Move(tid, "doing");           // same stage: nothing
        await Move(tid, "review");
        await Move(tid, "done");
        await Move(plain, "doing");
        Assert.Equal(3, e.Gw.Sent.Count(s => s.Event == "project.form_request_update" && s.User == e.Member.Id));
        Assert.DoesNotContain(e.Gw.Sent, s => s.User != e.Member.Id && s.Event == "project.form_request_update");
    }

    // ───────────── Close project ─────────────

    [Fact]
    public async Task Close_summary_math()
    {
        using var e = new Env();
        var past = DateTime.UtcNow.Date.AddDays(-20);
        await e.TaskAsync("a", "done", due: past);   // completed now -> 20 days late
        await e.TaskAsync("b", "done", due: DateTime.UtcNow.Date.AddDays(5));  // on time
        await e.TaskAsync("c", "done", due: DateTime.UtcNow.Date.AddDays(1));  // on time
        await e.TaskAsync("d", "done");              // done without due: ignored for on-time %
        var open = await e.TaskAsync("e", due: past.AddDays(-3));              // open + late 23 days
        e.T.Db.ProjectTaskComments.Add(new ProjectTaskComment { Id = Guid.NewGuid(), TaskId = open, AuthorId = e.Owner.Id, Content = "hi", CreatedAt = DateTime.UtcNow });
        var proj = e.T.Db.Projects.Find(e.P)!; proj.Deadline = DateTime.UtcNow.Date.AddDays(-2); e.T.Db.SaveChanges();

        var s = (await new ProjectCloseService(e.T.Verify()).GetSummaryAsync(e.P, e.Viewer.Id, default)).Value!;
        Assert.Equal(5, s.Total); Assert.Equal(4, s.Done); Assert.Equal(1, s.OpenTasks);
        Assert.Equal(67, s.OnTimePercent);
        Assert.Equal(2, s.LateCount);
        Assert.Equal(open, s.LongestDelay!.TaskId);
        Assert.Equal(1, s.OpenCommentsCount);
        Assert.Equal(2, s.FinishedDaysVsPlan);
        Assert.False(s.FilesOnNas);
        Assert.Equal("forbidden", (await new ProjectCloseService(e.T.Verify()).GetSummaryAsync(e.P, e.Outsider.Id, default)).Error);
    }

    [Fact]
    public async Task Close_and_reopen_with_template_lessons_and_chat()
    {
        using var e = new Env();
        await e.TaskAsync("open one");
        var projects = e.Projects();
        var svc = new ProjectCloseService(e.T.Verify(), new ProjectTemplateService(e.T.Verify(), projects));
        var room = e.T.Db.ProjectRooms.FirstOrDefault(r => r.ProjectId == e.P);
        Assert.NotNull(room);

        Assert.Equal("forbidden", (await svc.CloseAsync(e.P, e.Member.Id, new CloseProjectRequest(null, false, null, false), default)).Error);
        Assert.Equal("invalid_lessons", (await svc.CloseAsync(e.P, e.Owner.Id, new CloseProjectRequest(new string('x', 2001), false, null, false), default)).Error);

        var r = (await svc.CloseAsync(e.P, e.Owner.Id, new CloseProjectRequest("Plan earlier", true, "My tpl", true), default)).Value!;
        Assert.True(r.OpenTasksWarning);
        Assert.NotNull(r.TemplateId);
        Assert.True(r.SummaryPosted);
        var db = e.T.Verify();
        var p = db.Projects.Single(x => x.Id == e.P);
        Assert.Equal("completed", p.Status); Assert.NotNull(p.ClosedAt); Assert.Equal("Plan earlier", p.Lessons);
        var tpl = db.ProjectTemplates.Single(t => t.Id == r.TemplateId);
        Assert.Equal("private", tpl.Visibility); Assert.Contains("Plan earlier", tpl.Description);
        Assert.Single(db.ChatMessages, m => m.RoomId == room!.RoomId && m.Type == "system" && m.Content.Contains("Project closed"));

        Assert.Equal("forbidden", (await svc.ReopenAsync(e.P, e.Member.Id, default)).Error);
        Assert.True((await new ProjectCloseService(e.T.Verify()).ReopenAsync(e.P, e.Owner.Id, default)).Success);
        var again = e.T.Verify().Projects.Single(x => x.Id == e.P);
        Assert.Equal("active", again.Status); Assert.Null(again.ClosedAt);
    }

    // ───────────── Automations ─────────────

    [Fact]
    public async Task Automations_new_project_has_defaults_on_and_old_project_lazily_all_off()
    {
        using var e = new Env();
        var svc = new ProjectAutomationService(e.T.Verify());
        var list = (await svc.GetAsync(e.P, e.Viewer.Id, default)).Value!;
        Assert.Equal(6, list.Rules.Count);
        Assert.All(new[] { "stuck_notify_lead", "due_tomorrow_remind", "subtasks_done_review", "dependents_follow" }, k => Assert.True(list.Rules.Single(r => r.Kind == k).IsEnabled));
        Assert.False(list.Rules.Single(r => r.Kind == "weekly_recurring_task").IsEnabled);
        Assert.True(list.DependentsFollow);
        Assert.Equal("forbidden", (await svc.GetAsync(e.P, e.Outsider.Id, default)).Error);

        // A project made before the migration has no rows: nothing turns on by itself.
        var old = e.T.AddProject("Old", e.Owner.Id);
        var oldList = (await new ProjectAutomationService(e.T.Verify()).GetAsync(old.Id, e.Owner.Id, default)).Value!;
        Assert.All(oldList.Rules, r => Assert.False(r.IsEnabled));
        Assert.False(oldList.DependentsFollow);
    }

    [Fact]
    public async Task Automations_toggle_and_validation()
    {
        using var e = new Env();
        var svc = new ProjectAutomationService(e.T.Verify());
        Assert.Equal("forbidden", (await svc.SaveAsync(e.P, e.Member.Id, "stuck_notify_lead", new(false), default)).Error);
        Assert.Equal("automation_not_found", (await svc.SaveAsync(e.P, e.Owner.Id, "nope", new(true), default)).Error);
        Assert.False((await svc.SaveAsync(e.P, e.Owner.Id, "stuck_notify_lead", new(false), default)).Value!.IsEnabled);

        JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
        Assert.Equal("invalid_title", (await svc.SaveAsync(e.P, e.Owner.Id, "weekly_recurring_task", new(true, J("{\"title\":\"\",\"weekday\":1}")), default)).Error);
        Assert.Equal("invalid_title", (await svc.SaveAsync(e.P, e.Owner.Id, "weekly_recurring_task", new(true, J("{\"title\":\"" + new string('a', 201) + "\",\"weekday\":1}")), default)).Error);
        Assert.Equal("invalid_weekday", (await svc.SaveAsync(e.P, e.Owner.Id, "weekly_recurring_task", new(true, J("{\"title\":\"x\",\"weekday\":9}")), default)).Error);
        Assert.Equal("invalid_assignee", (await svc.SaveAsync(e.P, e.Owner.Id, "weekly_recurring_task", new(true, J("{\"title\":\"x\",\"weekday\":2,\"assigneeId\":\"" + e.Outsider.Id + "\"}")), default)).Error);
        var ok = (await svc.SaveAsync(e.P, e.Owner.Id, "weekly_recurring_task", new(true, J("{\"title\":\"Weekly report\",\"weekday\":3}")), default)).Value!;
        Assert.True(ok.IsEnabled);
        Assert.Equal("Weekly report", ok.Config.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Automation_form_to_intake_toggles_the_form()
    {
        using var e = new Env();
        var svc = new ProjectAutomationService(e.T.Verify());
        Assert.True((await svc.SaveAsync(e.P, e.Owner.Id, "form_to_intake", new(true), default)).Value!.IsEnabled);
        Assert.True(e.T.Verify().ProjectForms.Single(f => f.ProjectId == e.P).IsOpen);
        Assert.False((await svc.SaveAsync(e.P, e.Owner.Id, "form_to_intake", new(false), default)).Value!.IsEnabled);
        Assert.False(e.T.Verify().ProjectForms.Single(f => f.ProjectId == e.P).IsOpen);
    }

    [Fact]
    public async Task Automation_stuck_notifies_owner_once_per_day_and_counts_runs()
    {
        using var e = new Env();
        var id = await e.TaskAsync("blocked thing", assignees: e.Member.Id);
        await new ProjectStatusService(e.T.Verify()).EnsureStatusesAsync(e.P, default);
        var stuck = e.T.Verify().ProjectStatusOptions.Single(o => o.ProjectId == e.P && o.Label == "Stuck").Id;
        var working = e.T.Verify().ProjectStatusOptions.Single(o => o.ProjectId == e.P && o.Label == "Working on it").Id;
        e.Gw.Sent.Clear();

        await e.Projects().MoveTaskAsync(e.Member.Id, id, new TaskMoveRequest("doing", null, StatusOptionId: stuck), default);
        await e.Projects().MoveTaskAsync(e.Member.Id, id, new TaskMoveRequest("doing", null, StatusOptionId: working), default);
        await e.Projects().MoveTaskAsync(e.Member.Id, id, new TaskMoveRequest("doing", null, StatusOptionId: stuck), default);
        Assert.Single(e.Gw.Sent, s => s.Event == "project.task_stuck" && s.User == e.Owner.Id);
        var row = e.T.Verify().ProjectAutomations.Single(a => a.ProjectId == e.P && a.Kind == "stuck_notify_lead");
        Assert.Equal(1, row.RunCount); Assert.NotNull(row.LastRunAt);
        Assert.Single(e.T.Verify().ProjectTaskActivities, a => a.TaskId == id && a.Action == "automation:stuck_notified" && a.ActorId == e.Owner.Id);
    }

    [Fact]
    public async Task Automation_stuck_off_does_not_notify()
    {
        using var e = new Env();
        var id = await e.TaskAsync("t", assignees: e.Member.Id);
        await new ProjectAutomationService(e.T.Verify()).SaveAsync(e.P, e.Owner.Id, "stuck_notify_lead", new(false), default);
        var stuck = e.T.Verify().ProjectStatusOptions.Single(o => o.ProjectId == e.P && o.Label == "Stuck").Id;
        e.Gw.Sent.Clear();
        await e.Projects().MoveTaskAsync(e.Member.Id, id, new TaskMoveRequest("doing", null, StatusOptionId: stuck), default);
        Assert.DoesNotContain(e.Gw.Sent, s => s.Event == "project.task_stuck");
    }

    [Fact]
    public async Task Automation_subtasks_done_moves_parent_to_review_once()
    {
        using var e = new Env();
        var parent = await e.TaskAsync("parent", "doing");
        var db = e.T.Db;
        var group = db.ProjectGroups.First(g => g.ProjectId == e.P);
        var s1 = new ProjectTask { Id = Guid.NewGuid(), ProjectId = e.P, GroupId = group.Id, ParentTaskId = parent, Title = "s1", Status = "todo", CreatedById = e.Owner.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var s2 = new ProjectTask { Id = Guid.NewGuid(), ProjectId = e.P, GroupId = group.Id, ParentTaskId = parent, Title = "s2", Status = "todo", CreatedById = e.Owner.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.ProjectTasks.AddRange(s1, s2); db.SaveChanges();

        await e.Projects().MoveTaskAsync(e.Owner.Id, s1.Id, new TaskMoveRequest("done", null), default);
        Assert.Equal("doing", e.T.Verify().ProjectTasks.Single(t => t.Id == parent).Status);
        await e.Projects().MoveTaskAsync(e.Owner.Id, s2.Id, new TaskMoveRequest("done", null), default);
        var p = e.T.Verify().ProjectTasks.Single(t => t.Id == parent);
        Assert.Equal("review", p.Status);
        Assert.Equal("Ready to check", e.T.Verify().ProjectStatusOptions.Single(o => o.Id == p.StatusOptionId).Label);
        Assert.Equal(1, e.T.Verify().ProjectAutomations.Single(a => a.ProjectId == e.P && a.Kind == "subtasks_done_review").RunCount);
    }

    [Fact]
    public async Task Automation_worker_due_tomorrow_reminds_once_per_day_and_weekly_task_once_per_week()
    {
        using var e = new Env();
        var tomorrow = D(2026, 11, 19);
        await e.TaskAsync("soon", due: tomorrow, assignees: e.Member.Id);
        await e.TaskAsync("later", due: D(2026, 11, 25), assignees: e.Member.Id);
        JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
        await new ProjectAutomationService(e.T.Verify()).SaveAsync(e.P, e.Owner.Id, "weekly_recurring_task",
            new(true, J("{\"title\":\"Weekly report\",\"weekday\":3,\"assigneeId\":\"" + e.Member.Id + "\"}")), default);
        e.Gw.Sent.Clear();

        var local = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Local); // a Wednesday
        var utc = DateTime.UtcNow;
        await ProjectAutomationWorker.RunOnceAsync(e.T.Verify(), e.Gw, local, utc, NullLogger.Instance, default);
        await ProjectAutomationWorker.RunOnceAsync(e.T.Verify(), e.Gw, local.AddMinutes(1), utc.AddMinutes(1), NullLogger.Instance, default);
        Assert.Single(e.Gw.Sent, s => s.Event == "project.task_due_tomorrow" && s.User == e.Member.Id);
        var weekly = e.T.Verify().ProjectTasks.Where(t => t.Title == "Weekly report").ToList();
        Assert.Single(weekly);
        Assert.Equal(D(2026, 11, 18), weekly[0].DueDate);
        Assert.Equal(1, e.T.Verify().ProjectAutomations.Single(a => a.ProjectId == e.P && a.Kind == "weekly_recurring_task").RunCount);

        // Before 07:30 nothing runs; the wrong weekday does not create.
        await ProjectAutomationWorker.RunOnceAsync(e.T.Verify(), e.Gw, new DateTime(2026, 11, 25, 7, 0, 0, DateTimeKind.Local), utc.AddDays(7), NullLogger.Instance, default);
        Assert.Single(e.T.Verify().ProjectTasks.Where(t => t.Title == "Weekly report"));
        // Next Wednesday at 07:45 creates the next one.
        await ProjectAutomationWorker.RunOnceAsync(e.T.Verify(), e.Gw, new DateTime(2026, 11, 25, 7, 45, 0, DateTimeKind.Local), utc.AddDays(7), NullLogger.Instance, default);
        Assert.Equal(2, e.T.Verify().ProjectTasks.Count(t => t.Title == "Weekly report"));
    }

    // ───────────── Overview ─────────────

    [Fact]
    public async Task Overview_checklist_counts_burnup_attention_and_updates()
    {
        using var e = new Env();
        var svc = () => new ProjectOverviewService(e.T.Verify(), e.Gw);
        var empty = (await svc().GetOverviewAsync(e.P, e.Viewer.Id, Today, default)).Value!;
        Assert.Equal(["goal", "people", "dates", "baseline"], empty.Checklist.Select(c => c.Key));
        Assert.Equal([false, true, false, false], empty.Checklist.Select(c => c.Done));   // three members already
        Assert.Equal("forbidden", (await svc().GetOverviewAsync(e.P, e.Outsider.Id, Today, default)).Error);

        var p = e.T.Db.Projects.Find(e.P)!; p.Goal = "Ship it"; p.StartDate = D(2026, 11, 2); e.T.Db.SaveChanges();
        await e.TaskAsync("late one", due: D(2026, 11, 10), assignees: e.Member.Id);
        var doneId = await e.TaskAsync("done one", "done", due: D(2026, 11, 12), assignees: e.Member.Id);
        e.T.Db.ProjectTasks.Find(doneId)!.CompletedAt = D(2026, 11, 11); e.T.Db.SaveChanges();
        var stuckOpt = e.T.Db.ProjectStatusOptions.Single(o => o.ProjectId == e.P && o.Label == "Stuck");
        var stuckTask = await e.TaskAsync("stuck one", "doing", due: D(2026, 12, 30), assignees: e.Owner.Id);
        e.T.Db.ProjectTasks.Find(stuckTask)!.StatusOptionId = stuckOpt.Id; e.T.Db.SaveChanges();
        e.T.Db.ProjectBaselines.Add(new ProjectBaseline { Id = Guid.NewGuid(), ProjectId = e.P, Name = "Plan 1", CreatedAt = DateTime.UtcNow, CreatedById = e.Owner.Id, IsActive = true });
        e.T.Db.ProjectTaskActivities.Add(new ProjectTaskActivity { Id = Guid.NewGuid(), TaskId = doneId, ActorId = e.Member.Id, Action = "status_changed", NewValue = "done", CreatedAt = DateTime.UtcNow });
        e.T.Db.SaveChanges();

        var o = (await svc().GetOverviewAsync(e.P, e.Owner.Id, Today, default)).Value!;
        Assert.All(o.Checklist, c => Assert.True(c.Done));
        Assert.Equal(1, o.StatusCounts.Single(s => s.Label == "Stuck").Count);
        Assert.Contains(o.NeedsAttention, a => a.Reason == "late" && a.Title == "late one" && a.OwnerName == "User S4_M");
        Assert.Contains(o.NeedsAttention, a => a.Reason == "stuck" && a.Title == "stuck one");
        Assert.Equal(o.BurnUp.Weeks.Count, o.BurnUp.Plan.Count);
        Assert.True(o.BurnUp.Plan.Last() == 3);                                 // all three planned by the end
        Assert.Equal(1, o.BurnUp.Done.Where(x => x.HasValue).Last());           // one done so far
        Assert.Contains(o.LatestUpdates, u => u.Text.Contains("moved \"done one\" to done"));
        Assert.Equal(D(2026, 12, 30), o.ProjectFinish);
        Assert.Equal("User S4_O", o.LeadName);
    }

    [Fact]
    public async Task Overview_milestone_states()
    {
        using var e = new Env();
        var open = await e.TaskAsync("open", due: D(2026, 11, 25));
        var db = e.T.Db;
        db.ProjectMilestones.AddRange(
            new ProjectMilestone { Id = Guid.NewGuid(), ProjectId = e.P, Title = "Done", DueDate = D(2026, 11, 1), AchievedAt = D(2026, 11, 1), CreatedAt = DateTime.UtcNow },
            new ProjectMilestone { Id = Guid.NewGuid(), ProjectId = e.P, Title = "Risky", DueDate = D(2026, 11, 28), CreatedAt = DateTime.UtcNow, TaskId = open },
            new ProjectMilestone { Id = Guid.NewGuid(), ProjectId = e.P, Title = "Far", DueDate = D(2027, 3, 1), CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var o = (await new ProjectOverviewService(e.T.Verify()).GetOverviewAsync(e.P, e.Owner.Id, Today, default)).Value!;
        Assert.Equal(["achieved", "at_risk", "upcoming"], o.Milestones.OrderBy(m => m.Date).Select(m => m.State));
    }

    // ───────────── Weekly workload ─────────────

    [Fact]
    public async Task Workload_spreads_hours_over_working_days_skipping_weekends_holidays_and_splits_assignees()
    {
        using var e = new Env();
        // Fri 20 Nov -> Tue 24 Nov: working days Fri, Mon, Tue (3). 12h split over two assignees = 6h each = 2h/day.
        await e.TaskAsync("span", start: D(2026, 11, 20), due: D(2026, 11, 24), hours: 12, assignees: [e.Member.Id, e.Owner.Id]);
        await e.TaskAsync("no estimate", start: D(2026, 11, 17), due: D(2026, 11, 17), assignees: e.Member.Id);
        e.T.Db.WorkCalendarEntries.Add(new WorkCalendarEntry { Id = Guid.NewGuid(), ProjectId = e.P, Date = D(2026, 11, 24), Kind = "holiday", Title = "H", CreatedById = e.Owner.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        e.T.Db.SaveChanges();

        var w = (await new ProjectOverviewService(e.T.Verify()).GetWeeklyWorkloadAsync(e.P, e.Owner.Id, D(2026, 11, 16), Today, default)).Value!;
        Assert.Equal(8, w.Weeks.Count); Assert.Equal(40, w.CapacityHoursPerWeek);
        Assert.DoesNotContain(w.People, p => p.UserId == e.Viewer.Id);
        var m = w.People.Single(p => p.UserId == e.Member.Id);
        // Holiday Tue 24 removes a day: days = Fri 20 + Mon 23 = 2 -> 3h/day; week of 16 Nov gets Fri only (3h), 23 Nov gets Mon (3h).
        Assert.Equal(3m, m.Weeks[0].Hours); Assert.Equal(2, m.Weeks[0].Tasks);
        Assert.Equal(3m, m.Weeks[1].Hours); Assert.Equal(1, m.Weeks[1].Tasks);
        Assert.Equal(8, m.Weeks[0].Percent);
        Assert.False(m.Weeks[0].Off);
    }

    [Fact]
    public async Task Workload_marks_weeks_with_only_holidays_off_and_ignores_done_tasks()
    {
        using var e = new Env();
        for (var d = D(2026, 11, 23); d <= D(2026, 11, 27); d = d.AddDays(1))
            e.T.Db.WorkCalendarEntries.Add(new WorkCalendarEntry { Id = Guid.NewGuid(), ProjectId = e.P, Date = d, Kind = "offday", CreatedById = e.Owner.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await e.TaskAsync("finished", "done", start: D(2026, 11, 16), due: D(2026, 11, 20), hours: 40, assignees: e.Member.Id);
        e.T.Db.SaveChanges();
        var w = (await new ProjectOverviewService(e.T.Verify()).GetWeeklyWorkloadAsync(e.P, e.Member.Id, D(2026, 11, 16), Today, default)).Value!;
        Assert.True(w.People[0].Weeks[1].Off);
        Assert.False(w.People[0].Weeks[0].Off);
        Assert.All(w.People, p => Assert.Equal(0m, p.Weeks[0].Hours));
        Assert.Equal("forbidden", (await new ProjectOverviewService(e.T.Verify()).GetWeeklyWorkloadAsync(e.P, e.Outsider.Id, null, Today, default)).Error);
    }

    [Fact]
    public async Task Workload_suggest_moves_smallest_not_started_task_to_member_with_room_and_reassign_applies()
    {
        using var e = new Env();
        // Owner: 50h in week of 16 Nov (big started task 30h doing + two todo 12h + 8h).
        await e.TaskAsync("big doing", "doing", D(2026, 11, 16), D(2026, 11, 20), 30, e.Owner.Id);
        var small = await e.TaskAsync("small todo", "todo", D(2026, 11, 16), D(2026, 11, 20), 8, e.Owner.Id);
        await e.TaskAsync("medium todo", "todo", D(2026, 11, 16), D(2026, 11, 20), 12, e.Owner.Id);
        await e.TaskAsync("member busy", "todo", D(2026, 11, 16), D(2026, 11, 20), 36, e.Member.Id);
        var svc = () => new ProjectOverviewService(e.T.Verify(), e.Gw);

        var s = (await svc().SuggestAsync(e.P, e.Viewer.Id, new(e.Owner.Id, D(2026, 11, 18)), default)).Value!;
        // Owner over by 10h; only 4h free on Member -> neither 8h nor 12h fits.
        Assert.Empty(s.Suggestions);

        e.T.Db.ProjectTasks.First(t => t.Title == "member busy").EstimatedHours = 20; e.T.Db.SaveChanges();
        s = (await svc().SuggestAsync(e.P, e.Viewer.Id, new(e.Owner.Id, D(2026, 11, 18)), default)).Value!;
        // Owner is 10h over: the smallest (8h) moves first, then the 12h task still fits the remaining 12h free.
        Assert.Equal(2, s.Suggestions.Count);
        var only = s.Suggestions[0];
        Assert.Equal(small, only.TaskId); Assert.Equal(e.Member.Id, only.ToUserId); Assert.Equal(8m, only.Hours); Assert.Equal(20m, only.ToFreeHours);
        Assert.DoesNotContain(s.Suggestions, x => x.Title == "big doing");

        e.Gw.Sent.Clear();
        Assert.Equal("forbidden", (await svc().ReassignAsync(e.P, e.Viewer.Id, new(small, e.Owner.Id, e.Member.Id), default)).Error);
        Assert.Equal("invalid_assignee", (await svc().ReassignAsync(e.P, e.Owner.Id, new(small, e.Owner.Id, e.Viewer.Id), default)).Error);
        var actor = e.T.AddUser("S4_Z"); e.T.AddProjectMember(e.P, actor.Id, "manager");
        Assert.True((await svc().ReassignAsync(e.P, actor.Id, new(small, e.Owner.Id, e.Member.Id), default)).Success);
        Assert.Equal([e.Member.Id], e.T.Verify().ProjectTaskAssignees.Where(a => a.TaskId == small).Select(a => a.UserId).ToList());
        Assert.Single(e.Gw.Sent, x => x.Event == "project.task_assigned" && x.User == e.Member.Id);
        Assert.Single(e.Gw.Sent, x => x.Event == "project.task_unassigned" && x.User == e.Owner.Id);
    }

    // ───────────── Portfolio ─────────────

    [Fact]
    public async Task Portfolio_exposes_owner_department_milestone_and_explicit_health_wins()
    {
        using var e = new Env();
        var dept = e.T.EnsureDepartment("PD", "Ops"); e.T.AssignDepartment(e.Owner.Id, dept.Id);
        var p = e.T.Db.Projects.Find(e.P)!; p.Status = "active"; p.StartDate = D(2026, 11, 1); p.Deadline = D(2026, 12, 31); p.Health = "off_track"; e.T.Db.SaveChanges();
        e.T.Db.ProjectMilestones.Add(new ProjectMilestone { Id = Guid.NewGuid(), ProjectId = e.P, Title = "Go live", DueDate = D(2026, 12, 1), CreatedAt = DateTime.UtcNow });
        e.T.Db.SaveChanges();
        var other = e.T.AddProject("Fine", e.Owner.Id); other.Deadline = D(2027, 6, 1); e.T.Db.SaveChanges();

        var r = await new PortfolioService(e.T.Verify()).GetPortfolioAsync(e.Owner.Id, default, Today);
        var row = r.Projects.Items.Single(x => x.ProjectId == e.P);
        Assert.Equal("off_track", row.Health);
        Assert.Equal(D(2026, 11, 1), row.StartDate);
        Assert.Equal("Go live", row.NextMilestoneTitle); Assert.Equal(D(2026, 12, 1), row.NextMilestoneDate);
        Assert.Equal("User S4_O", row.OwnerName); Assert.Equal("S4_O", row.OwnerNik);
        Assert.Equal("Ops", row.DepartmentName); Assert.NotNull(row.Color);
        Assert.Equal(1, r.OffTrackProjects);
        Assert.Equal(1, r.OnTrackProjects);
    }
}
