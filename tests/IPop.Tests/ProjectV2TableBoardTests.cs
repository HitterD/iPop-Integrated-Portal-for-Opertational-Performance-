using System.Net;
using Bunit;
using IPop.Application.Projects;
using IPop.Web.Components.Projects;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Projects v2 stage 1 (web A): custom statuses drive the Table cells, the status popover and the Board columns.
public class ProjectV2TableBoardTests
{
    private sealed class EmptyApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new EmptyApi()) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new TimerState(auth));
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton<ToastService>();
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.Services.AddSingleton(new ConfirmService());
        return ctx;
    }

    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly ProjectStatusOptionDto NotStarted = new(Guid.NewGuid(), "Not started", "#c4c4c4", "todo", 0, 1);
    private static readonly ProjectStatusOptionDto Stuck = new(Guid.NewGuid(), "Stuck", "#e2445c", "doing", 1, 1);
    private static readonly ProjectStatusOptionDto Working = new(Guid.NewGuid(), "Working on it", "#fdab3d", "doing", 2, 0);
    private static readonly ProjectStatusOptionDto Done = new(Guid.NewGuid(), "Done", "#00c875", "done", 3, 0);
    private static readonly List<ProjectStatusOptionDto> Options = [NotStarted, Stuck, Working, Done];

    private static ProjectTaskDto MkTask(string title, string status, Guid? optionId) =>
        new(Guid.NewGuid(), Project, null, title, null, status, "medium", null, DateTime.Today.AddDays(3),
            null, null, 0, null, false, null, false, null, Guid.NewGuid(), "Owner",
            [], 0, 0, DateTime.UtcNow, DateTime.UtcNow, GroupId: GroupId, StatusOptionId: optionId);

    private static ProjectGroupDto Group() => new(GroupId, Project, "Phase 1", "#34C759", 0, 2, 0, 0);

    [Fact]
    public void Table_shows_each_tasks_custom_status_label_and_colour()
    {
        using var ctx = NewContext();
        var a = MkTask("Count stock", "todo", NotStarted.Id);
        var b = MkTask("Fix scanner", "doing", Stuck.Id);
        var cut = ctx.Render<ProjectTableView>(p => p
            .Add(x => x.Tasks, [a, b]).Add(x => x.Groups, [Group()]).Add(x => x.StatusOptions, Options));

        var cells = cut.FindAll(".pv2-st");
        Assert.Equal(new[] { "Not started", "Stuck" }, cells.Select(c => c.TextContent.Trim()));
        Assert.Contains("--c:#e2445c", cells[1].GetAttribute("style"));
    }

    [Fact]
    public void Table_row_has_one_square_checkbox_and_no_round_complete_circle()
    {
        using var ctx = NewContext();
        var t = MkTask("Count stock", "todo", NotStarted.Id);
        var cut = ctx.Render<ProjectTableView>(p => p
            .Add(x => x.Tasks, [t]).Add(x => x.Groups, [Group()]).Add(x => x.StatusOptions, Options));
        var row = cut.Find(".pv2-task");
        Assert.Single(row.QuerySelectorAll(".pv2-cb"));
        Assert.Empty(row.QuerySelectorAll(".pv2-check, .plist-check"));
        Assert.Equal("checkbox", row.QuerySelector(".pv2-cb")!.GetAttribute("role"));
        Assert.Equal("false", cut.Find(".pv2-row.pv2-head .pv2-cb").GetAttribute("aria-checked"));
    }

    [Fact]
    public void Default_not_started_grey_follows_the_theme_variable()
    {
        Assert.Equal("var(--s-todo)", ProjectStatusUi.SafeColor("#b9bcc0"));
        Assert.Equal("#00c875", ProjectStatusUi.SafeColor("#00c875"));
    }

    [Fact]
    public void Task_without_an_option_falls_back_to_the_first_option_of_its_stage()
    {
        using var ctx = NewContext();
        var t = MkTask("Legacy", "doing", null);
        var cut = ctx.Render<ProjectTableView>(p => p
            .Add(x => x.Tasks, [t]).Add(x => x.Groups, [Group()]).Add(x => x.StatusOptions, Options));
        Assert.Equal("Stuck", cut.Find(".pv2-st").TextContent.Trim());
    }

    [Fact]
    public void Status_popover_lists_custom_statuses_and_the_pick_carries_the_option_id()
    {
        using var ctx = NewContext();
        var t = MkTask("Count stock", "todo", NotStarted.Id);
        (ProjectTaskDto Task, string Status, Guid? Option)? picked = null;
        var cut = ctx.Render<ProjectTableView>(p => p
            .Add(x => x.Tasks, [t]).Add(x => x.Groups, [Group()]).Add(x => x.StatusOptions, Options)
            .Add(x => x.CanEditStatuses, true)
            .Add(x => x.OnStatusChangeDirect, ((ProjectTaskDto Task, string NewStatus, Guid? StatusOptionId) a) => picked = (a.Task, a.NewStatus, a.StatusOptionId)));

        cut.Find(".pv2-st").Click();
        var pills = cut.FindAll(".pv2-stpop .pv2-pill");
        Assert.Equal(new[] { "Not started", "Stuck", "Working on it", "Done" }, pills.Select(x => x.TextContent.Trim()));
        Assert.NotNull(cut.Find(".pv2-stpop .pv2-edit"));

        pills[1].Click();
        Assert.NotNull(picked);
        Assert.Equal("doing", picked!.Value.Status);
        Assert.Equal(Stuck.Id, picked.Value.Option);
    }

    [Fact]
    public void Edit_statuses_is_hidden_for_people_who_cannot_manage_the_project()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<ProjectTableView>(p => p
            .Add(x => x.Tasks, [MkTask("A", "todo", NotStarted.Id)]).Add(x => x.Groups, [Group()]).Add(x => x.StatusOptions, Options));
        cut.Find(".pv2-st").Click();
        Assert.Empty(cut.FindAll(".pv2-edit"));
    }

    [Fact]
    public void Board_has_one_column_per_custom_status_and_cards_sit_in_their_option_column()
    {
        using var ctx = NewContext();
        var a = MkTask("Count stock", "todo", NotStarted.Id);
        var b = MkTask("Fix scanner", "doing", Stuck.Id);
        var c = MkTask("Legacy", "doing", null); // first option of stage "doing" = Stuck
        var board = new KanbanBoardDto(Project, [a], [b, c], [], []);
        var cut = ctx.Render<ProjectKanbanBoard>(p => p.Add(x => x.Kanban, board).Add(x => x.StatusOptions, Options));

        var cols = cut.FindAll(".kanban-column");
        Assert.Equal(4, cols.Count);
        Assert.Equal(new[] { "Not started", "Stuck", "Working on it", "Done" }, cols.Select(x => x.QuerySelector(".col-title span")!.TextContent.Trim()));
        Assert.Equal(new[] { "1", "2", "0", "0" }, cols.Select(x => x.QuerySelector(".col-count")!.TextContent.Trim()));
    }

    [Fact]
    public void Board_card_menu_moves_with_the_option_id()
    {
        using var ctx = NewContext();
        var b = MkTask("Fix scanner", "doing", Stuck.Id);
        (Guid, string, Guid?)? moved = null;
        var cut = ctx.Render<ProjectKanbanBoard>(p => p
            .Add(x => x.Kanban, new KanbanBoardDto(Project, [], [b], [], []))
            .Add(x => x.StatusOptions, Options)
            .Add(x => x.OnTaskMove, ((Guid TaskId, string TargetStatus, Guid? StatusOptionId) a) => moved = (a.TaskId, a.TargetStatus, a.StatusOptionId)));
        cut.Find(".kc-more").Click();
        cut.Find("button[aria-label='Move to Working on it']").Click();
        Assert.Equal((b.Id, "doing", (Guid?)Working.Id), moved);
    }

    [Fact]
    public void Statuses_modal_shows_rows_and_is_read_only_without_edit_rights()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<ProjectStatusesModal>(p => p
            .Add(x => x.ProjectId, Project).Add(x => x.Options, Options).Add(x => x.CanEdit, false));
        Assert.Equal(4, cut.FindAll(".pv2-sel:not(.pv2-sel-head)").Count);
        Assert.All(cut.FindAll(".pv2-nm"), n => Assert.True(n.HasAttribute("disabled")));
        Assert.Empty(cut.FindAll(".pv2-add"));
    }
}
