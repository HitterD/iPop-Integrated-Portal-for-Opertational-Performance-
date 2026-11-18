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

/// Projects v2 stage 4 (web B): My work, All projects, request form, close project, rail buttons, header menu.
public class ProjectV2Stage4WebBTests
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
        public int Count(HttpMethod m, string suffix) { lock (Calls) return Calls.Count(c => c.Method == m && c.Path.EndsWith(suffix)); }
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

    private static readonly DateTime Today = new(2026, 11, 18); // a Wednesday; week ends Sun 22 Nov
    private static readonly Guid P1 = Guid.NewGuid(), T1 = Guid.NewGuid(), T2 = Guid.NewGuid(), T3 = Guid.NewGuid();

    // ───────── My work ─────────
    private static MyWorkDto MyWork() => new(new MyWorkCountsDto(1, 1, 1, 0, 9),
        [
            new(T1, P1, "ERP Rollout", "#2e9d6b", "Data migration", "Stuck", "#e2445c", "doing", Today.AddDays(-4), true, "Build"),
            new(T2, P1, "ERP Rollout", "#2e9d6b", "Sign scope", "Review", "#579bfc", "review", Today, false, "Plan"),
            new(T3, P1, "ERP Rollout", "#2e9d6b", "Clean vendor list", "Working", "#fdab3d", "doing", Today.AddDays(2), false, "Build"),
        ],
        [new MyWorkWaitingDto("request", "New request: Add Batch No", T2, P1, "ERP Rollout", "Rina", "0002", DateTime.UtcNow.AddMinutes(-10))]);

    private static HttpResponseMessage? MyWorkApi(HttpRequestMessage r, string _)
    {
        var path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/my-work")) return Json(MyWork());
        if (path.EndsWith("/statuses")) return Json(new[] { new ProjectStatusOptionDto(Guid.NewGuid(), "Done", "#00c875", "done", 3, 0) });
        if (path.EndsWith("/move")) return Json(new { success = true });
        return null;
    }

    private static IRenderedComponent<ProjectMyWorkPage> RenderMyWork(BunitContext ctx, Action<Guid, Guid>? open = null) =>
        ctx.Render<ProjectMyWorkPage>(p => p.Add(x => x.Today, Today)
            .Add(x => x.OnOpenTask, EventCallback.Factory.Create<(Guid ProjectId, Guid TaskId)>(ctx, t => open?.Invoke(t.ProjectId, t.TaskId))));

    [Fact]
    public void MyWork_renders_kpis_buckets_and_waiting()
    {
        var (ctx, _, _) = NewContext(MyWorkApi);
        using var _c = ctx;
        var cut = RenderMyWork(ctx);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".pv2mw-row").Count));
        Assert.Equal("1", cut.Find("#pv2mw-k-late").TextContent);
        Assert.Equal("9", cut.Find("#pv2mw-k-done").TextContent);
        Assert.Equal(4, cut.FindAll(".pv2mw-h").Count);
        Assert.Contains("Data migration", cut.Find("[data-bucket=Late]").NextElementSibling!.TextContent);
        Assert.Contains("New request: Add Batch No", cut.Find("[data-card=waiting]").TextContent);
        Assert.Contains("Nothing planned further ahead", cut.Markup);
    }

    [Fact]
    public void MyWork_tick_moves_task_removes_row_and_updates_counters()
    {
        var (ctx, api, toasts) = NewContext(MyWorkApi);
        using var _c = ctx;
        var cut = RenderMyWork(ctx);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".pv2mw-row").Count));
        cut.Find($"[data-task='{T1}'] [data-act=mwdone]").Click();
        cut.WaitForAssertion(() => Assert.Equal("0", cut.Find("#pv2mw-k-late").TextContent));
        Assert.Equal("10", cut.Find("#pv2mw-k-done").TextContent);
        Assert.Contains("gone", cut.Find($"[data-task='{T1}']").ClassName);
        Assert.Equal(1, api.Count(HttpMethod.Post, $"/tasks/{T1}/move"));
        var body = api.Calls.First(c => c.Path.EndsWith("/move")).Body;
        Assert.Contains("\"status\":\"done\"", body);
        Assert.Contains("Done. It moved to Done this week.", toasts.Active.Select(t => t.Message));
    }

    [Fact]
    public void MyWork_failed_tick_keeps_the_row()
    {
        var (ctx, _, toasts) = NewContext((r, b) => r.RequestUri!.AbsolutePath.EndsWith("/move") ? new HttpResponseMessage(HttpStatusCode.Forbidden) : MyWorkApi(r, b));
        using var _c = ctx;
        var cut = RenderMyWork(ctx);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".pv2mw-row").Count));
        cut.Find($"[data-task='{T2}'] [data-act=mwdone]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(toasts.Active));
        Assert.DoesNotContain("gone", cut.Find($"[data-task='{T2}']").ClassName);
        Assert.Equal("1", cut.Find("#pv2mw-k-today").TextContent);
    }

    [Fact]
    public void MyWork_row_click_opens_task_in_its_project()
    {
        var (ctx, _, _) = NewContext(MyWorkApi);
        using var _c = ctx;
        (Guid, Guid)? opened = null;
        var cut = RenderMyWork(ctx, (p, t) => opened = (p, t));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2mw-row")));
        cut.Find($"[data-task='{T3}']").Click();
        Assert.Equal((P1, T3), opened);
    }

    [Fact]
    public void MyWork_empty_and_error_states()
    {
        var (ctx, _, _) = NewContext((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/my-work") ? Json(new MyWorkDto(new(0, 0, 0, 0, 0), [], [])) : null);
        using var _c = ctx;
        var cut = RenderMyWork(ctx);
        cut.WaitForAssertion(() => Assert.Contains("You're all caught up", cut.Markup));
        var calls = 0;
        var (ctx2, _, _) = NewContext((r, _) => ++calls > 1 ? Json(MyWork()) : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var _c2 = ctx2;
        var cut2 = RenderMyWork(ctx2);
        cut2.WaitForAssertion(() => Assert.NotEmpty(cut2.FindAll(".pv2pg-empty button")));
        cut2.Find(".pv2pg-empty button").Click();
        cut2.WaitForAssertion(() => Assert.NotEmpty(cut2.FindAll(".pv2mw-row")));
    }

    [Fact]
    public void MyWork_buckets_and_counts_math()
    {
        var item = (DateTime? d, bool late) => new MyWorkItemDto(T1, P1, "P", "#000", "t", "s", "#000", "todo", d, late, "g");
        Assert.Equal(MyWorkMath.Bucket.Late, MyWorkMath.BucketOf(item(Today.AddDays(-1), true), Today));
        Assert.Equal(MyWorkMath.Bucket.Today, MyWorkMath.BucketOf(item(Today, false), Today));
        Assert.Equal(MyWorkMath.Bucket.ThisWeek, MyWorkMath.BucketOf(item(Today.AddDays(4), false), Today)); // Sun 22 Nov
        Assert.Equal(MyWorkMath.Bucket.Later, MyWorkMath.BucketOf(item(Today.AddDays(5), false), Today));
        Assert.Equal(MyWorkMath.Bucket.Later, MyWorkMath.BucketOf(item(null, false), Today));
        var c = MyWorkMath.Remove(new MyWorkCountsDto(1, 2, 3, 4, 5), MyWorkMath.Bucket.Today);
        Assert.Equal(new MyWorkCountsDto(1, 1, 3, 4, 6), c);
        Assert.Equal(42, MyWorkMath.MonthCells(Today).Count);
    }

    // ───────── All projects ─────────
    private static PortfolioProjectDto Pj(string name, string health, DateTime? start, DateTime? end, int late = 0) =>
        new(Guid.NewGuid(), name, "active", 40, end, 10, 4, late, health, start, "Go-live", end, "Bagas", "0001", "ICT", "#2e9d6b");

    [Fact]
    public void Portfolio_timeline_math_uses_min_start_to_max_deadline()
    {
        var rows = new[] { Pj("A", "on_track", Today.AddDays(-10), Today.AddDays(10)), Pj("B", "on_track", Today.AddDays(0), Today.AddDays(30)) };
        var s = PortfolioMath.ScaleFor(rows, Today)!;
        Assert.Equal(Today.AddDays(-10), s.Min);
        Assert.Equal(Today.AddDays(30), s.Max);
        Assert.Equal(25, s.TodayPct!.Value, 3);
        var bar = s.BarFor(rows[0])!;
        Assert.Equal(0, bar.Left, 3);
        Assert.Equal(50, bar.Width, 3);
        Assert.Null(PortfolioMath.ScaleFor([Pj("C", "on_track", null, null)], Today));
        Assert.Equal(("h2", "Off track"), PortfolioMath.Health("off_track"));
    }

    [Fact]
    public void Portfolio_renders_kpis_rows_and_opens_project()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var a = Pj("ERP Rollout", "at_risk", Today.AddDays(-10), Today.AddDays(40), late: 3);
        var data = new PortfolioDto("all", true, 7, 1, 40, 2, 3, new PagedResult<PortfolioProjectDto>([a, Pj("CCTV", "on_track", Today.AddDays(-5), Today.AddDays(5))], 2, 1, 20), [], [], [], 5, 1);
        Guid? opened = null;
        var cut = ctx.Render<ProjectPortfolioView>(p => p.Add(x => x.Data, data).Add(x => x.Today, Today)
            .Add(x => x.OnOpenProject, EventCallback.Factory.Create<Guid>(ctx, g => opened = g)));
        Assert.Equal("7", cut.Find("[data-kpi=active]").TextContent);
        Assert.Equal("5", cut.Find("[data-kpi=ontrack]").TextContent);
        Assert.Equal("1", cut.Find("[data-kpi=offtrack]").TextContent);
        Assert.Equal("1", cut.Find("[data-kpi=atrisk]").TextContent);
        var rows = cut.FindAll(".pv2pf-row[data-pid]");
        Assert.Equal(2, rows.Count);
        Assert.Contains("At risk", rows[0].TextContent);
        Assert.Contains("3", rows[0].QuerySelector(".pv2pf-late")!.TextContent);
        Assert.NotEmpty(rows[0].QuerySelectorAll(".pv2pf-ptl .now"));
        Assert.Contains("Go-live", rows[0].TextContent);
        rows[0].Click();
        Assert.Equal(a.ProjectId, opened);
    }

    // ───────── Request form ─────────
    private static FormQuestionDto Q(string key, string label, string type, string maps, bool req = false, string[]? opts = null) => new(key, label, type, req, opts, maps);

    private static ProjectFormDto Form(bool open = true) => new(P1, open, "Request for the ERP team", "Ask for a change.", "everyone", null, null, null,
        [Q("q1", "What do you need?", "text", "title", true), Q("q2", "Details", "longtext", "description"), Q("q3", "Plant", "choice", "none", false, ["Sepanjang", "Karawang"])], 0);

    private static PublicFormDto PublicForm() => new(P1, "ERP Rollout", "#2e9d6b", "Request for the ERP team", "Ask for a change.", Form().Questions);

    private static HttpResponseMessage? FormApi(HttpRequestMessage r, string body)
    {
        var path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/public")) return Json(PublicForm());
        if (path.EndsWith("/submit")) return Json(new SubmitFormResultDto(T1, "Add Batch No"));
        if (path.EndsWith("/form") && r.Method == HttpMethod.Get) return Json(Form());
        if (path.EndsWith("/form") && r.Method == HttpMethod.Put) return Json(Form());
        return null;
    }

    [Fact]
    public void Builder_shows_questions_and_audience_and_autosaves_edits()
    {
        var (ctx, api, _) = NewContext(FormApi);
        using var _c = ctx;
        var cut = ctx.Render<ProjectFormBuilder>(p => p.Add(x => x.ProjectId, P1).Add(x => x.CanManage, true).Add(x => x.ProjectName, "ERP"));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".pv2fm-q").Count));
        Assert.Contains("anyone at SJA (signed in)", cut.Find(".pv2fm-chip.static").TextContent);
        Assert.Contains("fills Task name", cut.Find("[data-q=q1] .pv2fm-map").TextContent);
        // Preview of the form is shown next to the builder.
        Assert.Contains("Preview: what requesters see", cut.Find(".pv2fm-right").TextContent);
        // edit: add a question + toggle accepting requests -> one debounced PUT with the new config
        cut.Find("[data-act=addq]").Click();
        Assert.Equal(4, cut.FindAll(".pv2fm-q").Count);
        cut.Find("[data-act=open]").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, api.Count(HttpMethod.Put, "/form")), TimeSpan.FromSeconds(4));
        var put = api.Calls.First(c => c.Method == HttpMethod.Put).Body;
        using var doc = JsonDocument.Parse(put);
        Assert.False(doc.RootElement.GetProperty("isOpen").GetBoolean());
        Assert.Equal(4, doc.RootElement.GetProperty("questions").GetArrayLength());
        cut.WaitForAssertion(() => Assert.Contains("Saved", cut.Find(".pv2-saved").TextContent));
    }

    [Fact]
    public void Builder_remove_and_reorder_questions()
    {
        var (ctx, api, _) = NewContext(FormApi);
        using var _c = ctx;
        var cut = ctx.Render<ProjectFormBuilder>(p => p.Add(x => x.ProjectId, P1).Add(x => x.CanManage, true));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".pv2fm-q").Count));
        cut.FindAll("[data-q=q1] .pv2-ibtn")[1].Click(); // move down
        Assert.Equal("q2", cut.FindAll(".pv2fm-q")[0].GetAttribute("data-q"));
        cut.Find("[data-q=q3] [data-act=rmq]").Click();
        Assert.Equal(2, cut.FindAll(".pv2fm-q").Count);
        cut.WaitForAssertion(() => Assert.Equal(1, api.Count(HttpMethod.Put, "/form")), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void Builder_preview_send_simulates_success_without_posting()
    {
        var (ctx, api, _) = NewContext(FormApi);
        using var _c = ctx;
        var cut = ctx.Render<ProjectFormBuilder>(p => p.Add(x => x.ProjectId, P1).Add(x => x.CanManage, true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2fm-right [data-act=formsend]")));
        cut.Find(".pv2fm-right input[type=text]").Input("Add Batch No");
        cut.Find(".pv2fm-right [data-act=formsend]").Click();
        Assert.Contains("Request sent", cut.Find(".pv2fm-right").TextContent);
        Assert.Equal(0, api.Count(HttpMethod.Post, "/submit"));
        cut.Find(".pv2fm-right [data-act=send-another]").Click();
        Assert.NotEmpty(cut.FindAll(".pv2fm-right [data-act=formsend]"));
    }

    [Fact]
    public void Member_sees_the_request_page_instead_of_the_builder()
    {
        var (ctx, api, _) = NewContext(FormApi);
        using var _c = ctx;
        var cut = ctx.Render<ProjectFormBuilder>(p => p.Add(x => x.ProjectId, P1).Add(x => x.CanManage, false));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-act=formsend]")));
        Assert.Empty(cut.FindAll(".pv2fm-tb"));
        Assert.Equal(0, api.Count(HttpMethod.Get, "/form"));
    }

    [Fact]
    public void Request_page_requires_answers_then_posts_and_shows_sent_state()
    {
        var (ctx, api, _) = NewContext(FormApi);
        using var _c = ctx;
        var cut = ctx.Render<ProjectRequestForm>(p => p.Add(x => x.ProjectId, P1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-act=formsend]")));
        cut.Find("[data-act=formsend]").Click();
        Assert.Contains("Please fill in", cut.Markup);
        Assert.Equal(0, api.Count(HttpMethod.Post, "/submit"));
        cut.Find("input[type=text]").Input("Add Batch No");
        cut.Find("[data-act=formsend]").Click();
        cut.WaitForAssertion(() => Assert.Contains("Request sent", cut.Markup));
        Assert.Contains("Add Batch No", cut.Find("[data-state=sent]").TextContent);
        var body = api.Calls.First(c => c.Path.EndsWith("/submit")).Body;
        Assert.Contains("\"q1\":\"Add Batch No\"", body);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "closed")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden")]
    [InlineData(HttpStatusCode.InternalServerError, "error")]
    public void Request_page_friendly_states(HttpStatusCode code, string state)
    {
        var (ctx, _, _) = NewContext((r, _) => new HttpResponseMessage(code));
        using var _c = ctx;
        var cut = ctx.Render<ProjectRequestForm>(p => p.Add(x => x.ProjectId, P1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"[data-state={state}]")));
    }

    // ───────── Close project ─────────
    private static CloseSummaryDto Summary(int open = 0) => new(41, 41 - open, 82, 5, 2, new LongestDelayDto(T1, "Vendor contract", 6), 2, open, true);

    private static HttpResponseMessage? CloseApi(HttpRequestMessage r, string _)
    {
        var path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/close-summary")) return Json(Summary(open: 2));
        if (path.EndsWith("/close")) return Json(new CloseProjectResultDto(Summary(), true, Guid.NewGuid(), true));
        return null;
    }

    [Fact]
    public void Close_modal_renders_summary_and_warnings()
    {
        var (ctx, _, _) = NewContext(CloseApi);
        using var _c = ctx;
        var cut = ctx.Render<ProjectCloseModal>(p => p.Add(x => x.ProjectId, P1).Add(x => x.ProjectName, "Gathering"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-fact=finish]")));
        Assert.Contains("Finished 2 days after the plan", cut.Find("[data-fact=finish]").TextContent);
        Assert.Contains("82%", cut.Find(".pv2cl-ring").TextContent);
        Assert.Contains("Vendor contract", cut.Find("[data-fact=delay]").TextContent);
        Assert.Contains("+6 days", cut.Find("[data-fact=delay]").TextContent);
        Assert.Contains("2 tasks are still open", cut.Find("[data-check=tasks]").TextContent);
        Assert.Contains("warn", cut.Find("[data-check=tasks]").ClassName);
        Assert.Contains("2 open comments", cut.Find("[data-check=comments]").TextContent);
        Assert.False(cut.Find("[data-act=archive]").HasAttribute("disabled"));
    }

    [Fact]
    public void Close_modal_posts_the_right_body_and_toasts()
    {
        var (ctx, api, toasts) = NewContext(CloseApi);
        using var _c = ctx;
        CloseProjectResultDto? closed = null;
        var cut = ctx.Render<ProjectCloseModal>(p => p.Add(x => x.ProjectId, P1).Add(x => x.ProjectName, "Gathering")
            .Add(x => x.OnClosed, EventCallback.Factory.Create<CloseProjectResultDto>(ctx, r => closed = r)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-act=archive]")));
        cut.Find("#pv2cl-lessons").Input("Book the venue earlier");
        cut.Find("[data-opt=template] button").Click();
        Assert.Equal("Gathering template", cut.Find("#pv2cl-tname").GetAttribute("value"));
        cut.Find("[data-opt=chat] button").Click();
        cut.Find("[data-act=archive]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(closed));
        using var doc = JsonDocument.Parse(api.Calls.First(c => c.Path.EndsWith("/close") && c.Method == HttpMethod.Post).Body);
        Assert.Equal("Book the venue earlier", doc.RootElement.GetProperty("lessons").GetString());
        Assert.True(doc.RootElement.GetProperty("saveAsTemplate").GetBoolean());
        Assert.Equal("Gathering template", doc.RootElement.GetProperty("templateName").GetString());
        Assert.True(doc.RootElement.GetProperty("postSummaryInChat").GetBoolean());
        Assert.Contains(toasts.Active, t => t.Message == "Gathering is closed and archived. Template saved.");
    }

    [Fact]
    public void Close_modal_failure_keeps_it_open()
    {
        var (ctx, _, toasts) = NewContext((r, b) => r.RequestUri!.AbsolutePath.EndsWith("/close") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : CloseApi(r, b));
        using var _c = ctx;
        var closed = false;
        var cut = ctx.Render<ProjectCloseModal>(p => p.Add(x => x.ProjectId, P1).Add(x => x.ProjectName, "G")
            .Add(x => x.OnClosed, EventCallback.Factory.Create<CloseProjectResultDto>(ctx, _ => closed = true)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-act=archive]")));
        cut.Find("[data-act=archive]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2-err")));
        Assert.False(closed);
        Assert.NotEmpty(cut.FindAll(".pv2cl-modal"));
        Assert.NotEmpty(toasts.Active);
    }

    // ───────── Header menu + rail ─────────
    private static ProjectDto Proj(string status) => new(P1, "ERP", null, 2026, Guid.NewGuid(), "Bagas", "0001", null, null, status, 40, null, null, true, 3, 10, 3, 3, 1, 3, DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public void Header_menu_offers_close_project_or_reopen()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var closeClicked = 0;
        var cut = ctx.Render<ProjectHeader>(p => p.Add(x => x.Project, Proj("active")).Add(x => x.V2, true).Add(x => x.CanManage, true)
            .Add(x => x.OnCloseProject, EventCallback.Factory.Create(ctx, () => closeClicked++)));
        cut.Find("button[aria-haspopup=menu]").Click();
        Assert.Contains("Close project", cut.Find(".ph-menu").TextContent);
        Assert.DoesNotContain("Mark project completed", cut.Find(".ph-menu").TextContent);
        cut.Find("[data-act=close]").Click();
        Assert.Equal(1, closeClicked);

        var reopened = 0;
        var cut2 = ctx.Render<ProjectHeader>(p => p.Add(x => x.Project, Proj("completed")).Add(x => x.V2, true).Add(x => x.CanManage, true)
            .Add(x => x.OnReopenProject, EventCallback.Factory.Create(ctx, () => reopened++)));
        cut2.Find("button[aria-haspopup=menu]").Click();
        Assert.Empty(cut2.FindAll("[data-act=close]"));
        cut2.Find("[data-act=reopen]").Click();
        Assert.Equal(1, reopened);
    }

    [Fact]
    public void Sidebar_has_my_work_with_late_badge_and_all_projects_buttons()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var list = new List<ProjectListDto>
        {
            new(P1, "ERP Rollout", 2026, "Bagas", "active", 40, null, 3, 10, true, "#2e9d6b"),
            new(Guid.NewGuid(), "Old thing", 2025, "Bagas", "active", 100, null, 3, 5, true, null),
        };
        int my = 0, all = 0; ProjectListDto? picked = null;
        var cut = ctx.Render<ProjectSidebar>(p => p.Add(x => x.Projects, list).Add(x => x.LateCount, 3).Add(x => x.MyWorkSelected, true)
            .Add(x => x.OnSelectMyWork, EventCallback.Factory.Create(ctx, () => my++))
            .Add(x => x.OnSelectPortfolio, EventCallback.Factory.Create(ctx, () => all++))
            .Add(x => x.OnSelectProject, EventCallback.Factory.Create<ProjectListDto>(ctx, x => picked = x)));
        Assert.Contains("3 late", cut.Find("[data-act=mywork]").TextContent);
        Assert.Contains("on", cut.Find("[data-act=mywork]").ClassName);
        cut.Find("[data-act=mywork]").Click();
        cut.Find("[data-act=portfolio]").Click();
        Assert.Equal((1, 1), (my, all));
        Assert.Equal(2, cut.FindAll(".pv2r-h").Count);
        var row = cut.Find($"[data-pid='{P1}']");
        Assert.Contains("40%", row.TextContent);
        Assert.DoesNotContain("active on", row.ClassName);
        row.Click();
        Assert.Equal(P1, picked!.Id);
    }
}
