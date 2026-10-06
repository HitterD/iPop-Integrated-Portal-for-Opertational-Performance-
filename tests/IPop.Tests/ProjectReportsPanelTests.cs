using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Application.Projects;
using IPop.Web.Components.Projects;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Reports tab (docs/mockups/ipop-apple/project-reports.html): burndown that only goes down as
/// work is finished, on-time % from the real completion date, milestone status mapping, and
/// milestones loaded once per project (not on every render).
public class ProjectReportsPanelTests
{
    // Wednesday 2026-10-07, 10:00 local.
    private static readonly DateTime Now = new(2026, 10, 7, 10, 0, 0, DateTimeKind.Local);

    private sealed class Api(List<MilestoneDto> milestones) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/milestones"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(milestones) });
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/achieve"))
            {
                var id = Guid.Parse(request.RequestUri.Segments[^2].TrimEnd('/'));
                var m = milestones.Single(x => x.Id == id) with { Status = MilestoneStatus.Achieved, AchievedAt = DateTime.UtcNow, AchievedManually = true };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(m) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static BunitContext NewContext(Api api, out ToastService toasts)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        toasts = new ToastService();
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }

    private static readonly Guid ProjectId = Guid.NewGuid();

    private static ProjectDto Project() => new(ProjectId, "Bug/Fitur iPop", null, 2026, Guid.NewGuid(), "Owner", "0001",
        null, null, "active", 0, null, null, true, 1, 0, 0, 0, 0, 0, DateTime.UtcNow, DateTime.UtcNow);

    private static ProjectTaskDto T(string title, DateTime created, DateTime? completed = null, DateTime? due = null) =>
        new(Guid.NewGuid(), ProjectId, null, title, null, completed is null ? "todo" : "done", "medium", null, due,
            null, null, 0, null, false, null, false, null, Guid.NewGuid(), "Owner",
            [], 0, 0, created.ToUniversalTime(), (completed ?? created).ToUniversalTime(),
            CompletedAt: completed?.ToUniversalTime());

    private static int[] Series(IRenderedComponent<ProjectReportsPanel> cut, string chart) =>
        cut.Find($"[data-chart='{chart}'] svg").GetAttribute("data-series")!.Split(',').Select(int.Parse).ToArray();

    [Fact]
    public void Burndown_never_rises_when_tasks_only_get_completed()
    {
        // 10 tasks created 13 weeks ago; one finished each week for the last 8 weeks.
        var start = Now.AddDays(-91);
        var tasks = Enumerable.Range(0, 10).Select(i =>
            T($"T{i}", start, i < 8 ? Now.AddDays(-7 * (8 - i) + 1) : null)).ToList();
        using var ctx = NewContext(new Api([]), out _);

        var cut = ctx.Render<ProjectReportsPanel>(p => p
            .Add(x => x.Project, Project()).Add(x => x.AllTasks, tasks).Add(x => x.Now, Now));

        var rem = Series(cut, "burndown");
        Assert.Equal(12, rem.Length);
        for (var i = 1; i < rem.Length; i++) Assert.True(rem[i] <= rem[i - 1], $"rose at {i}: {string.Join(",", rem)}");
        Assert.Equal(10, rem[0]);
        Assert.Equal(2, rem[^1]); // two tasks still open now

        // The old bug drew the axis upside down; the line must now end lower on screen = larger y.
        var path = cut.Find("[data-chart='burndown'] .act-line").GetAttribute("d")!;
        var ys = path.Split(['M', 'L'], StringSplitOptions.RemoveEmptyEntries).Select(pt => double.Parse(pt.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.True(ys[^1] > ys[0]);
    }

    [Fact]
    public void Burndown_counts_a_task_only_from_its_creation_week()
    {
        var tasks = new List<ProjectTaskDto>
        {
            T("old", Now.AddDays(-60)),
            T("new", Now.AddDays(-1)), // added this week
        };
        using var ctx = NewContext(new Api([]), out _);
        var cut = ctx.Render<ProjectReportsPanel>(p => p
            .Add(x => x.Project, Project()).Add(x => x.AllTasks, tasks).Add(x => x.Now, Now));

        cut.FindAll("[data-chart='burndown'] .seg button").First(b => b.TextContent.Trim() == "4 wk").Click();
        Assert.Equal([1, 1, 1, 2], Series(cut, "burndown"));
    }

    [Fact]
    public void On_time_uses_completion_date_against_end_of_due_day()
    {
        var due = Now.Date.AddDays(-10);
        var tasks = new List<ProjectTaskDto>
        {
            T("same day, late evening", Now.AddDays(-30), completed: due.AddHours(23), due: due),   // on time
            T("early", Now.AddDays(-30), completed: due.AddDays(-2), due: due),                      // on time
            T("a day late", Now.AddDays(-30), completed: due.AddDays(1).AddHours(9), due: due),     // late
            T("no due date", Now.AddDays(-30), completed: due),                                      // not counted
            T("open", Now.AddDays(-30), due: due),                                                   // overdue, not counted
        };
        using var ctx = NewContext(new Api([]), out _);
        var cut = ctx.Render<ProjectReportsPanel>(p => p
            .Add(x => x.Project, Project()).Add(x => x.AllTasks, tasks).Add(x => x.Now, Now));

        var tile = cut.Find("[data-tile='ontime']");
        Assert.Contains("67", tile.QuerySelector(".big")!.TextContent);
        Assert.Contains("2 of 3 done by their due date", tile.TextContent);
        Assert.Contains("1", cut.Find("[data-tile='overdue'] .big").TextContent);
        Assert.Contains("80", cut.Find("[data-tile='progress'] .big").TextContent); // 4 of 5 done
    }

    [Theory]
    [InlineData("achieved", "Achieved", "done")]
    [InlineData("overdue", "Overdue", "late")]
    [InlineData("due_soon", "Due soon", "soon")]
    [InlineData("planned", "Planned", "plan")]
    public void Milestone_status_maps_to_pill_and_row(string status, string label, string css)
    {
        Assert.Equal(css, ProjectReportsPanel.StatusKey(status));
        Assert.Equal(label, ProjectReportsPanel.PillLabel(css));
    }

    [Fact]
    public void Milestones_render_status_from_dates_and_linked_tasks_and_load_once()
    {
        var done1 = T("d1", Now.AddDays(-20), completed: Now.AddDays(-3));
        var done2 = T("d2", Now.AddDays(-20), completed: Now.AddDays(-2));
        var open = T("o", Now.AddDays(-20));
        var ms = new List<MilestoneDto>
        {
            // Server still says planned, but every linked task is done in the live list → achieved.
            new(Guid.NewGuid(), "All done", Now.Date.AddDays(30), null, [done1.Id, done2.Id], 2, 1, "planned", false),
            new(Guid.NewGuid(), "Late one", Now.Date.AddDays(-2), null, [open.Id], 1, 0, "overdue", false),
            new(Guid.NewGuid(), "Soon", Now.Date.AddDays(5), null, [open.Id], 1, 0, "due_soon", false),
            new(Guid.NewGuid(), "Later", Now.Date.AddDays(40), null, [], 0, 0, "planned", false),
            new(Guid.NewGuid(), "Marked", Now.Date.AddDays(60), DateTime.UtcNow, [open.Id], 1, 0, "achieved", true),
        };
        var api = new Api(ms);
        using var ctx = NewContext(api, out _);
        var tasks = new List<ProjectTaskDto> { done1, done2, open };
        var cut = ctx.Render<ProjectReportsPanel>(p => p
            .Add(x => x.Project, Project()).Add(x => x.AllTasks, tasks).Add(x => x.Now, Now));

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".ms").Count));
        string Status(string title) => cut.FindAll(".ms").Single(r => r.QuerySelector(".nm")!.TextContent == title).GetAttribute("data-status")!;
        Assert.Equal("achieved", Status("All done"));
        Assert.Equal("overdue", Status("Late one"));
        Assert.Equal("due_soon", Status("Soon"));
        Assert.Equal("planned", Status("Later"));
        Assert.Equal("achieved", Status("Marked"));
        Assert.Contains("2 of 5 achieved", cut.Markup);

        // Re-rendering with a new (equal) task list must not refetch milestones.
        cut.Render(p => p.Add(x => x.AllTasks, tasks.ToList()));
        cut.Render(p => p.Add(x => x.AllTasks, tasks.ToList()));
        Assert.Single(api.Calls, c => c.StartsWith("GET") && c.EndsWith("/milestones"));
    }

    [Fact]
    public void Mark_achieved_posts_and_offers_undo()
    {
        var open = T("o", Now.AddDays(-20));
        var m = new MilestoneDto(Guid.NewGuid(), "UAT ready", Now.Date.AddDays(10), null, [open.Id], 1, 0, "planned", false);
        var api = new Api([m]);
        using var ctx = NewContext(api, out var toasts);
        var cut = ctx.Render<ProjectReportsPanel>(p => p
            .Add(x => x.Project, Project()).Add(x => x.AllTasks, new List<ProjectTaskDto> { open }).Add(x => x.Now, Now));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ms")));

        cut.Find(".ms .act button").Click();

        cut.WaitForAssertion(() => Assert.Equal("achieved", cut.Find(".ms").GetAttribute("data-status")));
        Assert.Contains(api.Calls, c => c == $"POST /api/projects/{ProjectId}/milestones/{m.Id}/achieve");
        var toast = Assert.Single(toasts.Active);
        Assert.NotNull(toast.Undo);
        Assert.Contains("UAT ready", toast.Message);
    }

    [Fact]
    public void Empty_project_shows_empty_states_not_charts()
    {
        using var ctx = NewContext(new Api([]), out _);
        var cut = ctx.Render<ProjectReportsPanel>(p => p
            .Add(x => x.Project, Project()).Add(x => x.AllTasks, new List<ProjectTaskDto>()).Add(x => x.Now, Now));

        cut.WaitForAssertion(() => Assert.Contains("No milestones yet", cut.Markup));
        Assert.Contains("Nothing to burn down yet", cut.Markup);
        Assert.Contains("No completed tasks yet", cut.Markup);
        Assert.Empty(cut.FindAll(".cw svg"));
        Assert.DoesNotContain("Project Templates", cut.Markup);
    }
}
