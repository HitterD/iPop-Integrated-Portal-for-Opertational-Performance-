using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Application.Projects;
using IPop.Web.Components.Projects;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// New project, two steps (mockup projects-v2.html tplModal + setupModal): template cards filtered by
/// category and search, the live preview, the name/dates/people step, Back, and what Create submits.
public class ProjectCreateModalTemplateTests
{
    private static readonly DateTime Today = new(2026, 11, 2); // a Monday

    private static readonly Guid MeId = Guid.NewGuid();
    private static readonly Guid AndiId = Guid.NewGuid();

    private sealed class Api : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Calls) Calls.Add($"{request.Method} {path}");
            HttpResponseMessage Ok(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
            if (request.Method == HttpMethod.Delete) return Task.FromResult(Ok(new { success = true }));
            if (request.Method != HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (path == "/api/projects/templates") return Task.FromResult(Ok(Rows));
            if (path == "/api/projects/templates/categories")
                return Task.FromResult(Ok(new[] { new ProjectTemplateCategoryDto("ICT", 1), new ProjectTemplateCategoryDto("Events", 1), new ProjectTemplateCategoryDto("My templates", 1) }));
            if (path == "/api/projects/templates/scope") return Task.FromResult(Ok(new ProjectTemplateScopeDto(Guid.NewGuid(), "ICT")));
            if (path == $"/api/projects/templates/{Ict.Id}") return Task.FromResult(Ok(IctDetail));
            if (path == $"/api/projects/templates/{Event.Id}") return Task.FromResult(Ok(EventDetail));
            if (path == $"/api/projects/templates/{Mine.Id}") return Task.FromResult(Ok(EventDetail with { Id = Mine.Id, Name = Mine.Name }));
            if (path == "/api/directory/me")
                return Task.FromResult(Ok(new DirectoryEmployeeDto("0001", "Bagas", null, "ICT", "ICT", null, null, null, "b@x", null, null, "on", ["User"], UserId: MeId)));
            if (path == "/api/directory/employees")
                return Task.FromResult(Ok(new DirectoryPageDto(1, 1, 12, [new DirectoryEmployeeDto("0002", "Andi", null, "ICT", "ICT", null, null, null, "a@x", null, null, "on", UserId: AndiId)])));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static readonly ProjectTemplateDto Ict = new(Guid.NewGuid(), "ICT implementation", "Plan, build, test and go live", 22, null,
        4, 2, 14, true, "everyone", null, null, "zap", "inf", false, null, "ICT", 10);
    private static readonly ProjectTemplateDto Event = new(Guid.NewGuid(), "Office event", "Venue, budget and rundown", 26, null,
        3, 1, 9, true, "everyone", null, null, "calendar", "vio", false, null, "Events", 8);
    private static readonly ProjectTemplateDto Mine = new(Guid.NewGuid(), "Audit ISO team copy", null, 18, null,
        2, 0, 4, false, "department", "Sari", "QA", null, null, true, null, null, null);
    private static readonly ProjectTemplateDto[] Rows = [Ict, Event, Mine];

    private static readonly ProjectTemplateDetailDto IctDetail = new(Ict.Id, Ict.Name, Ict.Description,
        [new(Guid.NewGuid(), "Kick-off", null, null, 0, 0), new(Guid.NewGuid(), "Go-live", null, null, 10, 1)],
        [new(Guid.NewGuid(), "Planning", "#00c875", 0), new(Guid.NewGuid(), "Build", "#fdab3d", 1)],
        [new(Guid.NewGuid(), "Cut-over", 10, [])],
        [new("Not started", "#b9bcc0", "todo"), new("Working on it", "#fdab3d", "doing"), new("Done", "#00c875", "done")],
        ["overview", "table", "gantt"],
        [new("lead", "Project lead", "can manage the project", 4), new("engineer", "IT engineer", null, 11), new("keyuser", "Key user", null, 7)],
        ["Stuck → tell the lead"],
        [new("Planning", "#00c875", 5, 1, 2), new("Build", "#fdab3d", 8, 3, 6)],
        "ICT", 10, OffsetsInWorkingDays: true);
    private static readonly ProjectTemplateDetailDto EventDetail = new(Event.Id, Event.Name, Event.Description, [], [], [], null, null, null, null, null, "Events", 8);

    private static BunitContext NewContext(Api api)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton(new PhotoViewerState());
        ctx.Services.AddScoped<OverlayStack>();
        auth.LoadMeAsync().GetAwaiter().GetResult();
        return ctx;
    }

    private static IRenderedComponent<ProjectCreateModal> Open(BunitContext ctx, Action<ProjectCreateModal.CreateProjectModel>? onSubmit = null)
    {
        var cut = ctx.Render<ProjectCreateModal>(p => p
            .Add(x => x.Today, Today)
            .Add(x => x.OnSubmit, m => onSubmit?.Invoke(m)));
        cut.WaitForAssertion(() => Assert.True(cut.FindAll(".pv2n-tcard").Count >= 4), TimeSpan.FromSeconds(3));
        return cut;
    }

    [Fact]
    public void Step1_shows_blank_and_template_cards_with_used_counts()
    {
        using var ctx = NewContext(new Api());
        var cut = Open(ctx);
        var names = cut.FindAll(".pv2n-tcard b").Select(b => b.TextContent).ToList();
        Assert.Contains("Blank project", names[0]);
        Assert.Contains(names, n => n.Contains("ICT implementation") && n.Contains("used 14×"));
        Assert.Equal(2, cut.FindAll(".pv2n-tag").Count);   // "Built-in · SJA" on the two built-ins only
        Assert.Contains("Built-in · SJA", cut.Find(".pv2n-tag").TextContent);
        Assert.Contains("You can change everything later", cut.Markup);
    }

    [Fact]
    public async Task Category_and_search_filter_the_cards()
    {
        using var ctx = NewContext(new Api());
        var cut = Open(ctx);

        await cut.InvokeAsync(() => cut.FindAll(".pv2n-pi").First(b => b.TextContent.StartsWith("Events")).Click());
        var names = cut.FindAll(".pv2n-tcard b").Select(b => b.TextContent).ToList();
        Assert.Single(names);
        Assert.Contains("Office event", names[0]);

        await cut.InvokeAsync(() => cut.FindAll(".pv2n-pi").First(b => b.TextContent.StartsWith("All templates")).Click());
        await cut.InvokeAsync(() => cut.Find(".pv2n-tsearch input").Input("rundown"));
        Assert.Single(cut.FindAll(".pv2n-tcard b"));
        await cut.InvokeAsync(() => cut.Find(".pv2n-tsearch input").Input("zzzz"));
        Assert.Contains("No template matches", cut.Markup);
    }

    [Fact]
    public async Task Selecting_a_card_loads_the_detail_and_shows_statuses_views_and_roles()
    {
        var api = new Api();
        using var ctx = NewContext(api);
        var cut = Open(ctx);
        await cut.InvokeAsync(() => cut.FindAll(".pv2n-tcard").First(c => c.TextContent.Contains("ICT implementation")).Click());

        cut.WaitForAssertion(() => Assert.Contains("Working on it", cut.Find(".pv2n-tprev").TextContent));
        var prev = cut.Find(".pv2n-tprev");
        Assert.Contains("Project lead", prev.TextContent);
        Assert.Contains("IT engineer", prev.TextContent);
        Assert.Contains("Table", prev.TextContent);        // Views are labelled, not raw keys
        Assert.Contains("Stuck → tell the lead", prev.TextContent);
        Assert.Contains("week 3–6", prev.TextContent);
        Assert.Contains("~10", prev.TextContent);
        Assert.Contains($"GET /api/projects/templates/{Ict.Id}", api.Calls);
        Assert.Contains("on", cut.FindAll(".pv2n-tcard").First(c => c.TextContent.Contains("ICT implementation")).ClassList);
    }

    private static async Task<IRenderedComponent<ProjectCreateModal>> ToStep2(BunitContext ctx, Action<ProjectCreateModal.CreateProjectModel>? submit = null)
    {
        var cut = Open(ctx, submit);
        await cut.InvokeAsync(() => cut.FindAll(".pv2n-tcard").First(c => c.TextContent.Contains("ICT implementation")).Click());
        cut.WaitForAssertion(() => Assert.Contains("Working on it", cut.Find(".pv2n-tprev").TextContent));
        await cut.InvokeAsync(() => cut.Find(".pv2-mf .pv2-btn.pri").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2n-setup")));
        return cut;
    }

    [Fact]
    public async Task Use_this_template_goes_to_step2_with_the_name_prefilled_and_go_live_computed()
    {
        using var ctx = NewContext(new Api());
        var cut = await ToStep2(ctx);
        Assert.Equal("ICT implementation", cut.Find("#pv2n-name").GetAttribute("value"));
        // Monday 2 Nov + 10 working days = Monday 16 Nov.
        Assert.Contains("Mon 16 Nov 2026", cut.Find(".pv2n-fin").TextContent);
        Assert.Contains("2 task dates spread from 2 Nov to 16 Nov", cut.Find(".pv2n-calc").TextContent);
        // Roles with task counts; the lead role is pre-filled with the current user.
        var roles = cut.FindAll(".pv2n-role");
        Assert.Equal(3, roles.Count);
        Assert.Contains("11 tasks", roles[1].TextContent);
        Assert.Contains("Bagas", roles[0].TextContent);
        // Suggestions come from the caller's department, name + department only.
        cut.WaitForAssertion(() => Assert.Contains("Suggested from ICT", cut.Markup));
        Assert.DoesNotContain("0002", cut.Find(".pv2n-sugg").InnerHtml);
    }

    [Fact]
    public async Task Changing_the_start_date_recomputes_go_live_and_skips_weekends()
    {
        using var ctx = NewContext(new Api());
        var cut = await ToStep2(ctx);
        await cut.InvokeAsync(() => cut.Find("#pv2n-sd").Input("2026-11-07")); // Saturday: counts from Monday 9 Nov
        Assert.Contains("Mon 23 Nov 2026", cut.Find(".pv2n-fin").TextContent);
    }

    [Fact]
    public async Task Back_returns_to_step1_keeping_the_selection()
    {
        using var ctx = NewContext(new Api());
        var cut = await ToStep2(ctx);
        await cut.InvokeAsync(() => cut.FindAll(".pv2-mf .pv2-btn").First(b => b.TextContent.Contains("Back")).Click());
        var sel = cut.FindAll(".pv2n-tcard.on");
        Assert.Single(sel);
        Assert.Contains("ICT implementation", sel[0].TextContent);
    }

    [Fact]
    public async Task Suggestion_adds_a_person_to_the_last_role_and_create_submits_role_assignments()
    {
        ProjectCreateModal.CreateProjectModel? submitted = null;
        using var ctx = NewContext(new Api());
        var cut = await ToStep2(ctx, m => submitted = m);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2n-sugg button")));
        await cut.InvokeAsync(() => cut.Find(".pv2n-sugg button").Click());
        Assert.Contains("Andi", cut.FindAll(".pv2n-role")[2].TextContent);
        Assert.Contains("gone", cut.Find(".pv2n-sugg button").ClassList);

        await cut.InvokeAsync(() => cut.Find("#pv2n-goal").Input("Replace Excel with ERP."));
        await cut.InvokeAsync(() => cut.Find(".pv2-mf .pv2-btn.pri").Click());

        Assert.NotNull(submitted);
        Assert.Equal(Ict.Id, submitted!.TemplateId);
        Assert.Equal("Replace Excel with ERP.", submitted.Goal);
        Assert.Equal(new DateTime(2026, 11, 2), submitted.StartDate);
        Assert.Equal(DateTimeKind.Utc, submitted.StartDate!.Value.Kind);
        Assert.Equal([MeId], submitted.RoleAssignments!["lead"]);
        Assert.Equal([AndiId], submitted.RoleAssignments["keyuser"]);
    }

    [Fact]
    public async Task Blank_project_path_has_no_roles_and_submits_without_a_template()
    {
        ProjectCreateModal.CreateProjectModel? submitted = null;
        using var ctx = NewContext(new Api());
        var cut = Open(ctx, m => submitted = m);
        await cut.InvokeAsync(() => cut.FindAll(".pv2n-tcard")[0].Click());
        await cut.InvokeAsync(() => cut.Find(".pv2-mf .pv2-btn.pri").Click());
        Assert.Empty(cut.FindAll(".pv2n-role"));
        await cut.InvokeAsync(() => cut.Find("#pv2n-name").Input("Attendance digitization"));
        await cut.InvokeAsync(() => cut.Find(".pv2-mf .pv2-btn.pri").Click());
        Assert.NotNull(submitted);
        Assert.Null(submitted!.TemplateId);
        Assert.Null(submitted.RoleAssignments);
        Assert.Equal("Attendance digitization", submitted.Name);
    }

    [Fact]
    public async Task Create_is_disabled_while_busy()
    {
        using var ctx = NewContext(new Api());
        var cut = ctx.Render<ProjectCreateModal>(p => p.Add(x => x.Today, Today).Add(x => x.Busy, true));
        cut.WaitForAssertion(() => Assert.True(cut.FindAll(".pv2n-tcard").Count >= 4), TimeSpan.FromSeconds(3));
        await cut.InvokeAsync(() => cut.FindAll(".pv2n-tcard")[0].Click());
        await cut.InvokeAsync(() => cut.Find(".pv2-mf .pv2-btn.pri").Click());
        await cut.InvokeAsync(() => cut.Find("#pv2n-name").Input("X"));
        Assert.NotNull(cut.Find(".pv2-mf .pv2-btn.pri").GetAttribute("disabled"));
    }

    [Fact]
    public async Task Owner_can_delete_a_team_template_from_the_cards()
    {
        var api = new Api();
        using var ctx = NewContext(api);
        var cut = Open(ctx);
        // Only the template the caller may delete gets a ⋯ menu.
        Assert.Single(cut.FindAll(".pv2n-more"));
        await cut.InvokeAsync(() => cut.Find(".pv2n-more").Click());
        await cut.InvokeAsync(() => cut.Find(".pv2n-tmenu button").Click());
        cut.WaitForAssertion(() => Assert.DoesNotContain("Audit ISO team copy", cut.Markup));
        Assert.Contains($"DELETE /api/projects/templates/{Mine.Id}", api.Calls);
    }
}
