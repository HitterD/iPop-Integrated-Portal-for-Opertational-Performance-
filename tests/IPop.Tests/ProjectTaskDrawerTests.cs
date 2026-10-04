using System.Net;
using Bunit;
using IPop.Application.Projects;
using IPop.Web.Components.Projects;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// The task drawer is the single task UI in Projects (Quick view was folded into it):
/// status pills, "n / N" pager, overdue banner, a visible Evidence section, and menus that
/// portal out of the scrolling lanes.
public class ProjectTaskDrawerTests
{
    private sealed class EmptyApi : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static BunitContext NewContext(out ConfirmService confirm, EmptyApi? api = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api ?? new EmptyApi()) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new TimerState(auth));
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton<ToastService>();
        ctx.Services.AddScoped<OverlayStack>();
        confirm = new ConfirmService();
        ctx.Services.AddSingleton(confirm);
        return ctx;
    }

    private static ProjectTaskDto MakeTask(string title, string status = "todo", DateTime? due = null, string? description = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, title, description, status, "medium", null, due,
            null, null, 0, null, false, null, false, null, Guid.NewGuid(), "Owner",
            [], 0, 0, DateTime.UtcNow, DateTime.UtcNow);

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IPop.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    [Fact]
    public void Status_is_one_click_pills_and_a_click_goes_through_the_page_move_flow()
    {
        using var ctx = NewContext(out _);
        var task = MakeTask("Wire rack", "todo");
        var moves = new List<(Guid, string)>();
        var cut = ctx.Render<ProjectTaskDrawer>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.OnStatusChange, ((ProjectTaskDto Task, string NewStatus) a) => moves.Add((a.Task.Id, a.NewStatus))));

        Assert.Empty(cut.FindAll("select.td-status, .app-select.td-status"));
        var pills = cut.FindAll(".td-status-pill");
        Assert.Equal(new[] { "To Do", "In Progress", "Review", "Done" }, pills.Select(p => p.TextContent.Trim()));
        Assert.Equal("true", cut.Find(".td-status-pill.todo").GetAttribute("aria-checked"));

        cut.Find(".td-status-pill.done").Click();
        Assert.Equal((task.Id, "done"), Assert.Single(moves));

        cut.Find(".td-status-pill.todo").Click(); // already the saved status: no-op
        Assert.Single(moves);
    }

    [Fact]
    public void Pager_shows_position_in_the_view_order_and_steps_to_the_neighbour()
    {
        using var ctx = NewContext(out _);
        var a = MakeTask("A"); var b = MakeTask("B"); var c = MakeTask("C");
        ProjectTaskDto? navigated = null;
        var cut = ctx.Render<ProjectTaskDrawer>(p => p
            .Add(x => x.Task, b)
            .Add(x => x.NavTasks, new List<ProjectTaskDto> { a, b, c })
            .Add(x => x.OnNavigate, (ProjectTaskDto t) => navigated = t));

        Assert.Equal("2 / 3", cut.Find(".td-pager-pos").TextContent.Trim());
        cut.Find("button[aria-label='Next task']").Click();
        Assert.Same(c, navigated);
        cut.Find("button[aria-label='Previous task']").Click();
        Assert.Same(a, navigated);
    }

    [Fact]
    public void Pager_hides_when_the_task_is_not_in_the_current_view()
    {
        using var ctx = NewContext(out _);
        var cut = ctx.Render<ProjectTaskDrawer>(p => p
            .Add(x => x.Task, MakeTask("Solo"))
            .Add(x => x.NavTasks, new List<ProjectTaskDto> { MakeTask("Other") }));
        Assert.Empty(cut.FindAll(".td-pager"));
    }

    [Fact]
    public void Pager_asks_before_discarding_unsaved_edits()
    {
        using var ctx = NewContext(out var confirm);
        var a = MakeTask("A"); var b = MakeTask("B");
        ProjectTaskDto? navigated = null;
        var cut = ctx.Render<ProjectTaskDrawer>(p => p
            .Add(x => x.Task, a)
            .Add(x => x.NavTasks, new List<ProjectTaskDto> { a, b })
            .Add(x => x.OnNavigate, (ProjectTaskDto t) => navigated = t));

        cut.Find("input.td-title").Change("A (edited)");
        Assert.Contains("Unsaved changes", cut.Find(".td-foot").TextContent);

        cut.Find("button[aria-label='Next task']").Click();
        Assert.NotNull(confirm.Current);
        confirm.Resolve(false);
        cut.WaitForAssertion(() => Assert.Null(navigated));

        cut.Find("button[aria-label='Next task']").Click();
        confirm.Resolve(true);
        cut.WaitForAssertion(() => Assert.Same(b, navigated));
    }

    [Fact]
    public void Overdue_task_shows_banner_with_extension_reason_and_one_extend_button()
    {
        using var ctx = NewContext(out _);
        var evidence = new TaskEvidenceData();
        evidence.ExtensionHistory.Add(new TaskExtensionRecord { NewDueDate = DateTime.Today.AddDays(-2), Reason = "Vendor late" });
        var task = MakeTask("Late", "doing", DateTime.Today.AddDays(-2), TaskEvidenceHelper.Serialize("desc", evidence));
        ProjectTaskDto? extended = null;
        var cut = ctx.Render<ProjectTaskDrawer>(p => p
            .Add(x => x.Task, task)
            .Add(x => x.OnRequestExtension, (ProjectTaskDto t) => extended = t));

        var banner = cut.Find(".td-overdue");
        Assert.Contains("Overdue", banner.TextContent);
        Assert.Contains("Vendor late", banner.TextContent);
        Assert.Empty(cut.FindAll(".td-extend")); // no duplicate Extend in the Due row
        cut.Find(".td-overdue-btn").Click();
        Assert.Same(task, extended);
    }

    [Fact]
    public void Task_not_overdue_has_no_banner_and_keeps_the_due_row_extend()
    {
        using var ctx = NewContext(out _);
        var cut = ctx.Render<ProjectTaskDrawer>(p => p.Add(x => x.Task, MakeTask("Fine", "todo", DateTime.Today.AddDays(5))));
        Assert.Empty(cut.FindAll(".td-overdue"));
        Assert.Single(cut.FindAll(".td-extend"));
    }

    [Fact]
    public void Evidence_is_its_own_section_and_hours_stay_under_additional_details()
    {
        using var ctx = NewContext(out _);
        var cut = ctx.Render<ProjectTaskDrawer>(p => p.Add(x => x.Task, MakeTask("E")));

        var section = cut.Find("section.td-evidence");
        Assert.Equal(2, section.QuerySelectorAll(".task-evidence-section").Length);
        Assert.Null(section.Closest("details"));
        var extra = cut.Find("details.td-extra");
        Assert.NotNull(extra.QuerySelector(".td-hours"));
        Assert.Null(extra.QuerySelector(".task-evidence-section"));
        Assert.DoesNotContain("Auto-save", cut.Markup);
    }

    [Fact]
    public void Actual_hours_are_not_typed_and_time_has_its_own_section()
    {
        using var ctx = NewContext(out _);
        var cut = ctx.Render<ProjectTaskDrawer>(p => p.Add(x => x.Task, MakeTask("T")));

        Assert.DoesNotContain("Actual hours", cut.Markup);
        Assert.Single(cut.Find(".td-hours").QuerySelectorAll("input"));
        var time = cut.Find("section.td-time");
        Assert.Null(time.Closest("details"));
        Assert.Contains("TIME", time.TextContent);
    }

    [Fact]
    public void Quick_view_is_gone_from_the_page_and_the_task_menus()
    {
        var page = RepoFile("src/IPop.Web/Pages/Projects.razor");
        Assert.DoesNotContain("ProjectTaskQuickViewer", page);
        Assert.DoesNotContain("QuickView", page);
        Assert.DoesNotContain("quick viewer", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OnInspectTask=\"OpenTaskEditor\"", page);
        foreach (var f in new[] { "ProjectKanbanBoard.razor", "ProjectTableView.razor" })
        {
            var src = RepoFile("src/IPop.Web/Components/Projects/" + f);
            Assert.DoesNotContain("Quick view", src);
            Assert.DoesNotContain("OnQuickViewTask", src);
        }
    }

    [Fact]
    public void Row_and_card_menus_portal_out_of_scroll_containers()
    {
        foreach (var f in new[] { "ProjectKanbanBoard.razor", "ProjectTableView.razor", "ProjectTaskDrawer.razor" })
        {
            var src = RepoFile("src/IPop.Web/Components/Projects/" + f);
            Assert.Contains("ipop.menuSync", src);
            Assert.Contains("CloseMenuFromJs", src);
        }
        var js = RepoFile("src/IPop.Web/wwwroot/js/ipop.js");
        Assert.Contains("menuOpen(pop, dotnet, align)", js);
        Assert.Contains("menuSync(pop, dotnet, align)", js);
    }

    [Fact]
    public void Kanban_card_menu_lists_every_action_without_quick_view()
    {
        using var ctx = NewContext(out _);
        var task = MakeTask("Card", "doing", DateTime.Today.AddDays(3));
        var board = new KanbanBoardDto(task.ProjectId, [], [task], [], []);
        var cut = ctx.Render<ProjectKanbanBoard>(p => p.Add(x => x.Kanban, board));

        cut.Find(".kc-more").Click();
        var menu = cut.Find(".kc-menu[role=menu]");
        var labels = menu.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();
        Assert.Contains("Open details", labels);
        Assert.Contains("Assign members", labels);
        Assert.Contains("Request extension", labels);
        Assert.DoesNotContain("Quick view", labels);
        Assert.Equal("true", cut.Find(".kc-more").GetAttribute("aria-expanded"));
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "ipop.menuSync");
    }
}
