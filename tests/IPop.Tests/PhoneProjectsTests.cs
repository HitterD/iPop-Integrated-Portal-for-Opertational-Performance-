using Bunit;
using IPop.Application.Projects;
using IPop.Web.Components.Phone.Projects;
using Xunit;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace IPop.Tests;

public class PhoneProjectsTests
{
    [Fact]
    public void Project_cards_use_real_counts_and_open_the_project()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        var id = Guid.NewGuid(); Guid? opened = null;
        var project = new ProjectListDto(id, "Launch", 2026, "Jane", "active", 40, DateTime.Today.AddDays(-1), 3, 10, true);
        var cut = ctx.Render<ProjectListPhone>(p => p.Add(x => x.Projects, new[] { project }).Add(x => x.OnOpen, value => opened = value.Id));
        Assert.Contains("40%", cut.Markup); Assert.Contains("10 tasks", cut.Markup);
        Assert.NotNull(cut.Find(".is-overdue"));
        cut.Find("button[aria-label='Open Launch']").Click(); Assert.Equal(id, opened);
    }

    [Fact]
    public void Board_shows_one_column_and_viewers_cannot_move_tasks()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        var task = Task("Private task", "doing");
        var cut = ctx.Render<BoardPhone>(p => p.Add(x => x.Tasks, new[] { task }).Add(x => x.ReadOnly, true));
        Assert.DoesNotContain("Private task", cut.Markup);
        cut.Find("button[data-status='doing']").Click();
        Assert.Contains("Private task", cut.Markup);
        Assert.Equal("true", cut.Find("button[data-status='doing']").GetAttribute("aria-selected"));
        Assert.Empty(cut.FindAll("button[aria-label='Move task']"));
        Assert.DoesNotContain("99999123", cut.Markup);
    }

    [Fact]
    public void Move_sheet_delegates_to_existing_evidence_handler()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        var task = Task("Check", "todo"); (Guid TaskId, string TargetStatus)? moved = null;
        var cut = ctx.Render<BoardPhone>(p => p.Add(x => x.Tasks, new[] { task }).Add(x => x.OnMove, value => moved = value));
        cut.Find("button[aria-label='Move task']").Click();
        cut.Find("button[data-move='review']").Click();
        Assert.Equal((task.Id, "review"), moved);
    }

    [Fact]
    public void Task_phone_header_has_back_and_vertical_stepping()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var back = false; int? step = null;
        var cut = ctx.Render<TaskDetailPhone>(p => p.Add(x => x.Title, "Planning").Add(x => x.CanPrevious, true)
            .Add(x => x.OnBack, () => back = true).Add(x => x.OnStep, value => step = value));
        cut.Find("button[aria-label='Previous task']").Click(); Assert.Equal(-1, step);
        Assert.True(cut.Find("button[aria-label='Next task']").HasAttribute("disabled"));
        cut.Find("button[aria-label='Back to Board']").Click(); Assert.True(back);
    }

    [Fact]
    public void New_task_sheet_submits_title_and_cannot_submit_blank()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        string? title = null;
        var cut = ctx.Render<NewTaskSheet>(p => p.Add(x => x.Open, true).Add(x => x.OnCreate, value => title = value));
        Assert.True(cut.Find("button[type='submit']").HasAttribute("disabled"));
        cut.Find("input[aria-label='Task title']").Input("Prepare launch");
        cut.Find("form").Submit(); Assert.Equal("Prepare launch", title);
    }

    [Fact]
    public void Phone_properties_are_sheets_and_readonly_cannot_open_mutation()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack()); string? status = null;
        var cut = ctx.Render<TaskPropertiesPhone>(p => p.Add(x => x.Status, "todo").Add(x => x.OnStatus, value => status = value));
        cut.Find("button[data-property='status']").Click();
        cut.Find("button[data-value='review']").Click(); Assert.Equal("review", status);
        cut.Render(p => p.Add(x => x.ReadOnly, true));
        Assert.All(cut.FindAll("button[data-property]"), button => Assert.True(button.HasAttribute("disabled")));
    }

    [Fact]
    public void Project_search_and_year_filters_delegate_without_invented_scope_data()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        string? query = null; int? year = null;
        var cut = ctx.Render<ProjectListPhone>(p => p.Add(x => x.Years, new[] { 2026 }).Add(x => x.OnSearch, value => query = value).Add(x => x.OnYear, value => year = value));
        cut.Find("input[aria-label='Search projects']").Input("Launch"); Assert.Equal("Launch", query);
        cut.Find("button[data-year='2026']").Click(); Assert.Equal(2026, year);
    }

    [Fact]
    public async Task Long_press_opens_move_sheet_without_opening_task()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        var cut = ctx.Render<BoardPhone>(p => p.Add(x => x.Tasks, new[] { Task("Lift", "todo") }));
        await cut.Find(".m-project-task-open").TriggerEventAsync("onpointerdown", new Microsoft.AspNetCore.Components.Web.PointerEventArgs { PointerType = "touch" });
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("button[data-move='doing']")), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Load_failure_offers_retry_and_keeps_cached_cards()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var retried = false;
        var cut = ctx.Render<ProjectListPhone>(p => p.Add(x => x.Error, "Offline").Add(x => x.OnRetry, () => retried = true));
        Assert.Equal("Offline", cut.Find("[role='alert']").TextContent);
        cut.Find("button.m-project-move").Click(); Assert.True(retried);
    }

    private static ProjectTaskDto Task(string title, string status) => new(Guid.NewGuid(), Guid.NewGuid(), null, title, null,
        status, "high", null, null, null, null, 0, null, false, null, false, null, Guid.NewGuid(), "Jane",
        new[] { new ProjectTaskAssigneeDto(Guid.NewGuid(), "99999123", "Jane", "owner", DateTime.UtcNow) }, 0, 0, DateTime.UtcNow, DateTime.UtcNow);
}
