using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using IPop.Application.Projects;
using IPop.Web.Components.Projects;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Projects v2 stage 4 (web A): Overview, weekly Workload, task side panel, Automations modal and header badge.
public class ProjectV2Stage4WebTests
{
    private sealed class Api : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path, string Body)> Calls = [];
        public Func<HttpRequestMessage, string, HttpResponseMessage?>? Override;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((r.Method, r.RequestUri!.AbsolutePath, body));
            if (Override?.Invoke(r, body) is { } o) return o;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };

    private static (BunitContext Ctx, Api Api, ToastService Toasts) NewContext(Func<HttpRequestMessage, string, HttpResponseMessage?>? over = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new Api { Override = over };
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        var toasts = new ToastService();
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new TimerState(auth));
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(new PeopleLinks());
        ctx.Services.AddSingleton<CalendarLinks>();
        ctx.Services.AddSingleton<ConfirmService>();
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.Services.AddScoped<OverlayStack>();
        ctx.Services.AddSingleton(new ChatAttachments(auth, http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(new MediaStream(auth, http));
        return (ctx, api, toasts);
    }

    // ───────── Overview ─────────
    private static ProjectOverviewDto Overview(bool allDone = false) => new(
        "Replace Excel with ERP", DateTime.Today.AddDays(-10), DateTime.Today.AddDays(40), DateTime.Today.AddDays(40), "Bagas", "/ICT/ERP",
        [new("goal", "Add the goal", true), new("people", "Invite key users", allDone), new("dates", "Check Gantt dates", true), new("baseline", "Save a baseline", allDone)],
        [new("Not started", "#c4c4c4", "todo", 3), new("Stuck", "#e2445c", "doing", 2), new("Done", "#00c875", "done", 5)],
        new OverviewBurnUpDto(["1 Nov", "8 Nov", "15 Nov"], [0, 5, 10], [0, 4, null]),
        [new(Guid.NewGuid(), "Scope signed", DateTime.Today, "achieved", null), new(Guid.NewGuid(), "Go-live", DateTime.Today.AddDays(30), "at_risk", "Past its date"), new(Guid.NewGuid(), "UAT", DateTime.Today.AddDays(20), "upcoming", null)],
        [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Data migration", "Stuck", "#e2445c", "stuck", "Dewi", "0002"), new(Guid.NewGuid(), "UAT prep", "Working", "#fdab3d", "overloaded", "Andi", "0003")],
        [new("Dewi", "0002", "marked \"Data migration\" Stuck", DateTime.UtcNow.AddHours(-1))]);

    [Fact]
    public void Overview_renders_cards_chart_and_attention_rows()
    {
        var (ctx, _, _) = NewContext((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/overview") ? Json(Overview()) : null);
        using var _c = ctx;
        Guid? opened = null;
        var cut = ctx.Render<ProjectOverviewView>(p => p.Add(x => x.ProjectId, Guid.NewGuid()).Add(x => x.CanManage, true)
            .Add(x => x.OnOpenTask, EventCallback.Factory.Create<Guid>(this, g => opened = g)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-card=goal]")));
        Assert.Contains("Replace Excel with ERP", cut.Find("[data-card=goal]").TextContent);
        Assert.Contains("Bagas", cut.Find("[data-card=goal]").TextContent);
        Assert.Contains("2 of 4", cut.Find("[data-card=checklist]").TextContent);
        Assert.Equal("Burn-up: planned 10 tasks, 4 done so far", cut.Find("svg.pv2o-chart").GetAttribute("aria-label"));
        Assert.Equal("img", cut.Find("svg.pv2o-chart").GetAttribute("role"));
        Assert.Single(cut.FindAll("polyline.plan"));
        Assert.Single(cut.FindAll("polyline.done"));
        Assert.Equal(3, cut.FindAll(".pv2o-mst").Count);
        Assert.Single(cut.FindAll(".pv2o-mst.ok"));
        Assert.Single(cut.FindAll(".pv2o-mst.risk"));
        Assert.Equal(3, cut.FindAll("[data-card=progress] .pv2o-counts > div").Count);
        Assert.Contains("50%", cut.Find(".pv2o-ring").TextContent);
        var rows = cut.FindAll("[data-card=attention] .pv2o-risk");
        Assert.Equal(2, rows.Count);
        Assert.Contains("Overloaded", rows[1].TextContent);
        rows[0].Click();
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), opened);
        Assert.Contains("Dewi", cut.Find("[data-card=updates]").TextContent);
    }

    [Fact]
    public void Overview_checklist_hides_when_all_done()
    {
        var (ctx, _, _) = NewContext((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/overview") ? Json(Overview(allDone: true)) : null);
        using var _c = ctx;
        var cut = ctx.Render<ProjectOverviewView>(p => p.Add(x => x.ProjectId, Guid.NewGuid()));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-card=goal]")));
        Assert.Empty(cut.FindAll("[data-card=checklist]"));
    }

    [Fact]
    public void Overview_error_offers_retry()
    {
        var calls = 0;
        var (ctx, _, _) = NewContext((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/overview") && ++calls > 1 ? Json(Overview()) : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var _c = ctx;
        var cut = ctx.Render<ProjectOverviewView>(p => p.Add(x => x.ProjectId, Guid.NewGuid()));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2o-error")));
        cut.Find(".pv2o-error button").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-card=goal]")));
    }

    [Fact]
    public void Burn_math_builds_points_and_stops_done_line_at_today()
    {
        var b = ProjectOverviewMath.Build(new OverviewBurnUpDto(["a", "b", "c"], [0, 5, 10], [0, 4, null]));
        Assert.Equal(3, b.PlanPoints.Split(' ').Length);
        Assert.Equal(2, b.DonePoints.Split(' ').Length);
        Assert.StartsWith("M40,150", b.AreaPath);
    }

    // ───────── Weekly workload ─────────
    private static readonly Guid Andi = Guid.NewGuid(), Yanti = Guid.NewGuid(), TaskId = Guid.NewGuid(), ProjId = Guid.NewGuid();

    private static WeeklyWorkloadDto Weekly() => new(
        Enumerable.Range(0, 8).Select(i => new WorkloadWeekDto(new DateTime(2026, 11, 16).AddDays(7 * i), "x")).ToList(),
        [new WorkloadPersonDto(Andi, "Andi", "0003", "member",
            [new(52, 3, 130, false), new(20, 1, 50, false), new(32, 2, 80, false), new(0, 0, 0, false), new(0, 0, 0, false), new(0, 0, 0, true), new(0, 0, 0, true), new(10, 1, 25, false)])],
        40);

    private static HttpResponseMessage? WorkloadApi(HttpRequestMessage r, string body)
    {
        var path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/workload/weekly")) return Json(Weekly());
        if (path.EndsWith("/workload/suggest")) return Json(new WorkloadSuggestionsDto([new WorkloadSuggestionDto(TaskId, "UAT preparation", 12, Yanti, "Yanti Putri", 26)]));
        if (path.EndsWith("/workload/reassign")) return Json(new { success = true });
        return null;
    }

    [Fact]
    public void Workload_grid_classes_off_weeks_and_tasks_mode()
    {
        var (ctx, _, _) = NewContext(WorkloadApi);
        using var _c = ctx;
        var cut = ctx.Render<ProjectWeeklyWorkloadView>(p => p.Add(x => x.ProjectId, ProjId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2w-load")));
        var cells = cut.FindAll(".pv2w-grid .pv2w-row:not(.h) .pv2w-load");
        Assert.Equal(8, cells.Count);
        Assert.Contains("l3", cells[0].ClassList);
        Assert.Contains("l1", cells[1].ClassList);
        Assert.Contains("l2", cells[2].ClassList);
        Assert.Contains("l0", cells[3].ClassList);
        Assert.Contains("off", cells[5].ClassList);
        Assert.Contains("Year-end break", cells[5].TextContent);
        Assert.Contains("over by 12 h", cells[0].TextContent);
        Assert.Contains("40 h / week", cut.Markup);
        Assert.Equal("button", cells[0].TagName.ToLowerInvariant());
        cut.Find(".pv2w-seg button:nth-of-type(2)").Click();
        Assert.Contains("3 tasks", cut.FindAll(".pv2w-load")[0].TextContent);
    }

    [Fact]
    public void Workload_over_capacity_click_suggests_and_apply_reassigns_with_undo()
    {
        var (ctx, api, toasts) = NewContext(WorkloadApi);
        using var _c = ctx;
        var cut = ctx.Render<ProjectWeeklyWorkloadView>(p => p.Add(x => x.ProjectId, ProjId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.pv2w-load.l3")));
        cut.Find("button.pv2w-load.l3").Click();
        cut.WaitForAssertion(() => Assert.Contains("Move to Yanti", cut.Find(".pv2w-pop").TextContent));
        Assert.Contains("Andi has 52 h in W47", cut.Find(".pv2w-pop").TextContent);
        Assert.Contains("UAT preparation", cut.Find(".pv2w-pop").TextContent);
        cut.Find(".pv2w-pop .pv2-btn.pri").Click();
        cut.WaitForAssertion(() => Assert.Contains(api.Calls, c => c.Path.EndsWith("/workload/reassign")));
        var call = api.Calls.First(c => c.Path.EndsWith("/workload/reassign"));
        Assert.Contains(Andi.ToString(), call.Body);
        Assert.Contains(Yanti.ToString(), call.Body);
        cut.WaitForAssertion(() => Assert.Single(toasts.Active));
        Assert.NotNull(toasts.Active[0].Undo);
        toasts.Active[0].Undo!.Invoke().GetAwaiter().GetResult();
        var back = api.Calls.Where(c => c.Path.EndsWith("/workload/reassign")).ToList();
        Assert.Equal(2, back.Count);
        Assert.Contains(Yanti.ToString(), back[1].Body[..back[1].Body.IndexOf("toUserId", StringComparison.OrdinalIgnoreCase)]);
    }

    [Fact]
    public void Workload_viewers_get_no_click_affordance()
    {
        var (ctx, _, _) = NewContext(WorkloadApi);
        using var _c = ctx;
        var cut = ctx.Render<ProjectWeeklyWorkloadView>(p => p.Add(x => x.ProjectId, ProjId).Add(x => x.ReadOnly, true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2w-load")));
        Assert.Empty(cut.FindAll("button.pv2w-load"));
        Assert.Contains("l3", cut.FindAll(".pv2w-load")[0].ClassList);
    }

    [Fact]
    public void Workload_cell_class_thresholds()
    {
        Assert.Equal("l1", ProjectWeeklyWorkloadView.CellClass(new(10, 1, 74, false)));
        Assert.Equal("l2", ProjectWeeklyWorkloadView.CellClass(new(30, 1, 75, false)));
        Assert.Equal("l2", ProjectWeeklyWorkloadView.CellClass(new(40, 1, 100, false)));
        Assert.Equal("l3", ProjectWeeklyWorkloadView.CellClass(new(41, 1, 101, false)));
        Assert.Equal("l0", ProjectWeeklyWorkloadView.CellClass(new(0, 0, 0, true)));
    }

    // ───────── Task panel ─────────
    private static ProjectTaskDto MakeTask(string title, string status = "doing", DateTime? due = null, IReadOnlyList<DependencyRefDto>? deps = null) =>
        new(Guid.NewGuid(), ProjId, null, title, null, status, "high", null, due, 16, null, 0, null, false, null, false, null, Guid.NewGuid(), "Owner",
            [new ProjectTaskAssigneeDto(Guid.NewGuid(), "0002", "Dewi Sari", "assignee", DateTime.UtcNow)], 2, 1, DateTime.UtcNow, DateTime.UtcNow, DependsOn: deps);

    private static IRenderedComponent<ProjectTaskPanel> RenderPanel(BunitContext ctx, ProjectTaskDto t, IReadOnlyList<ProjectTaskDto>? subs = null,
        bool ro = false, EventCallback? onClose = null, EventCallback<ProjectTaskDto>? onToggle = null, IReadOnlyList<ProjectTaskDto>? nav = null, EventCallback<ProjectTaskDto>? onNav = null)
    {
        // The panel paints header + tabs first and the body one frame later.
        var cut = ctx.Render<ProjectTaskPanel>(p =>
        {
            p.Add(x => x.Task, t).Add(x => x.Subtasks, subs ?? []).Add(x => x.ReadOnly, ro).Add(x => x.Today, DateTime.Today)
             .Add(x => x.Options, ProjectStatusUi.StageDefaults).AllTasksOf(nav ?? [t]).Add(x => x.NavTasks, nav ?? [t]);
            if (onClose is { } c) p.Add(x => x.OnClose, c);
            if (onToggle is { } tg) p.Add(x => x.OnToggleSubtask, tg);
            if (onNav is { } n) p.Add(x => x.OnNavigate, n);
        });
        cut.WaitForState(() => cut.FindAll(".pv2p-b .pv2p-kvs").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    [Fact]
    public void Panel_renders_tabs_details_and_late_days()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var t = MakeTask("Data migration", due: DateTime.Today.AddDays(-3));
        var dep = new DependencyRefDto(Guid.NewGuid(), "Sign scope", "done", "FS", 2);
        t = t with { DependsOn = [dep] };
        var cut = RenderPanel(ctx, t, [MakeTask("A", "done"), MakeTask("B", "todo")]);
        Assert.Equal(new[] { "details", "updates", "activity" }, cut.FindAll(".pv2p-tabs [role=tab]").Select(x => x.GetAttribute("data-tab")).ToArray());
        Assert.Contains("3 days late", cut.Markup);
        Assert.Contains("Dewi", cut.Markup);
        Assert.Contains("1/2", cut.Find("#pv2p-clc").TextContent);
        var depRow = cut.Find("[data-sec=dependencies] .pv2p-dep");
        Assert.Contains("Waits for", depRow.TextContent);
        Assert.Contains("Finish → Start +2d", depRow.TextContent);
        Assert.NotEmpty(cut.FindAll("[data-act=open-full]"));
        Assert.Contains("16 h estimate", cut.Find("[data-sec=time]").TextContent);
        cut.Find("[data-tab=activity]").Click();
        Assert.Empty(cut.FindAll("[data-sec=checklist]"));
    }

    [Fact]
    public void Panel_checklist_tick_calls_the_toggle_callback()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var subs = new[] { MakeTask("Export items", "todo"), MakeTask("Check items", "done") };
        ProjectTaskDto? ticked = null;
        var cut = RenderPanel(ctx, MakeTask("Parent"), subs, onToggle: EventCallback.Factory.Create<ProjectTaskDto>(this, s => ticked = s));
        var boxes = cut.FindAll("[data-sec=checklist] .pv2-cb");
        Assert.Equal(2, boxes.Count);
        boxes[0].Click();
        Assert.Equal(subs[0].Id, ticked!.Id);
        Assert.Contains("on", boxes[1].ClassList);
    }

    [Fact]
    public void Panel_escape_closes_and_arrows_step_through_the_view_order()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var a = MakeTask("A"); var b = MakeTask("B"); var c = MakeTask("C");
        var closed = 0; ProjectTaskDto? navTo = null;
        var cut = RenderPanel(ctx, b, null, false, EventCallback.Factory.Create(this, () => closed++), null, [a, b, c], EventCallback.Factory.Create<ProjectTaskDto>(this, x => navTo = x));
        cut.Find("#pv2-panel").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(1, closed);
        cut.InvokeAsync(() => cut.Instance.PanelKey("ArrowDown")).GetAwaiter().GetResult();
        Assert.Equal(c.Id, navTo!.Id);
        cut.InvokeAsync(() => cut.Instance.PanelKey("ArrowUp")).GetAwaiter().GetResult();
        Assert.Equal(a.Id, navTo!.Id);
        cut.InvokeAsync(() => cut.Instance.PanelKey("Escape")).GetAwaiter().GetResult();
        Assert.Equal(2, closed);
    }

    [Fact]
    public void Panel_read_only_hides_editing_affordances()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var cut = RenderPanel(ctx, MakeTask("X"), [MakeTask("S", "todo")], ro: true);
        Assert.Empty(cut.FindAll("[data-act=add-owner]"));
        Assert.Empty(cut.FindAll("[data-act=add-dep]"));
        Assert.Empty(cut.FindAll(".pv2p-add"));
        Assert.True(cut.Find("[data-sec=checklist] .pv2-cb").HasAttribute("disabled"));
    }

    // ───────── Automations ─────────
    private static AutomationListDto Rules(bool formOn = false) => new(
        [new("stuck_notify_lead", "Tell the lead", "d", true, 6, DateTime.UtcNow, JsonSerializer.SerializeToElement(new { statusLabel = "Stuck" })),
         new("due_tomorrow_remind", "Remind", "d", true, 14, null, JsonSerializer.SerializeToElement(new { })),
         new("form_to_intake", "Requests land in Intake", "d", formOn, 0, null, JsonSerializer.SerializeToElement(new { })),
         new("weekly_recurring_task", "Weekly", "d", false, 0, null, JsonSerializer.SerializeToElement(new { title = "Stock check", weekday = 1 })),
         new("dependents_follow", "Dependents follow", "d", true, 2, null, JsonSerializer.SerializeToElement(new { }))], true);

    [Fact]
    public void Automations_modal_lists_rules_and_toggle_puts()
    {
        var list = Rules();
        var (ctx, api, _) = NewContext((r, body) =>
        {
            var p = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Get && p.EndsWith("/automations")) return Json(list);
            if (r.Method == HttpMethod.Put && p.EndsWith("/automations/form_to_intake")) return Json(list.Rules[2] with { IsEnabled = true });
            return null;
        });
        using var _c = ctx;
        AutomationListDto? changed = null;
        var cut = ctx.Render<ProjectAutomationsModal>(p => p.Add(x => x.ProjectId, ProjId).Add(x => x.CanManage, true)
            .Add(x => x.Options, ProjectStatusUi.StageDefaults).Add(x => x.OnChanged, EventCallback.Factory.Create<AutomationListDto>(this, a => changed = a)));
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".pv2au-rule").Count));
        Assert.Contains("Ran 6× so far", cut.Find("[data-kind=stuck_notify_lead]").TextContent);
        Assert.Contains("Every Monday 07:30", cut.Find("[data-kind=weekly_recurring_task]").TextContent);
        Assert.Contains("off", cut.Find("[data-kind=weekly_recurring_task]").ClassList);
        cut.Find("[data-kind=form_to_intake] .pv2au-tog").Click();
        cut.WaitForAssertion(() => Assert.NotNull(changed));
        var put = api.Calls.Single(c => c.Method == HttpMethod.Put);
        Assert.EndsWith("/automations/form_to_intake", put.Path);
        Assert.Contains("\"isEnabled\":true", put.Body);
        Assert.Equal(4, changed!.Rules.Count(x => x.IsEnabled));
    }

    [Fact]
    public void Automations_modal_is_read_only_for_non_managers_and_builder_adds_weekly_rule()
    {
        var list = Rules();
        var (ctx, api, _) = NewContext((r, _) =>
            r.Method == HttpMethod.Get ? Json(list) : r.Method == HttpMethod.Put ? Json(list.Rules[3] with { IsEnabled = true }) : null);
        using var _c = ctx;
        var ro = ctx.Render<ProjectAutomationsModal>(p => p.Add(x => x.ProjectId, ProjId).Add(x => x.CanManage, false));
        ro.WaitForAssertion(() => Assert.Equal(5, ro.FindAll(".pv2au-rule").Count));
        Assert.All(ro.FindAll(".pv2au-tog"), t => Assert.True(t.HasAttribute("disabled")));
        Assert.Empty(ro.FindAll("[data-sec=builder]"));

        var cut = ctx.Render<ProjectAutomationsModal>(p => p.Add(x => x.ProjectId, ProjId).Add(x => x.CanManage, true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-act=add-rule]")));
        cut.Find("input[aria-label='Task title']").Input("Weekly stock check");
        cut.Find("[data-act=add-rule]").Click();
        cut.WaitForAssertion(() => Assert.Contains(api.Calls, c => c.Method == HttpMethod.Put));
        var put = api.Calls.Last(c => c.Method == HttpMethod.Put);
        Assert.EndsWith("/automations/weekly_recurring_task", put.Path);
        Assert.Contains("Weekly stock check", put.Body);
        Assert.Contains("\"weekday\":1", put.Body);
    }

    [Fact]
    public void Header_shows_automate_badge_with_enabled_count()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var proj = new ProjectDto(Guid.NewGuid(), "ERP", null, 2026, Guid.NewGuid(), "Owner", "0001", null, null, "active", 40, null, null, true, 3, 10, 2, 2, 2, 4, DateTime.UtcNow, DateTime.UtcNow);
        var opened = 0;
        var cut = ctx.Render<ProjectHeader>(p => p.Add(x => x.V2, true).Add(x => x.Project, proj).Add(x => x.Members, new List<ProjectMemberDto>())
            .Add(x => x.AutomationCount, 4).Add(x => x.OnOpenAutomations, EventCallback.Factory.Create(this, () => opened++)));
        Assert.Equal("4", cut.Find(".pv2-auto .pv2-badge").TextContent.Trim());
        cut.Find(".pv2-auto").Click();
        Assert.Equal(1, opened);
        cut.Render(p => p.Add(x => x.AutomationCount, 3));
        Assert.Equal("3", cut.Find(".pv2-auto .pv2-badge").TextContent.Trim());
        Assert.Contains("flash", cut.Find(".pv2-auto .pv2-badge").ClassList);
    }
}

internal static class PanelTestExt
{
    public static ComponentParameterCollectionBuilder<ProjectTaskPanel> AllTasksOf(this ComponentParameterCollectionBuilder<ProjectTaskPanel> b, IReadOnlyList<ProjectTaskDto> all)
        => b.Add(x => x.AllTasks, all);
}
