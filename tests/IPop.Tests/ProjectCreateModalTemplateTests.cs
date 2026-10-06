using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Web.Components.Projects;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// New project → Template (mockup artboard 2): built-in and team groups, the counts line,
/// the "Creates N tasks in M sections · K milestones, dates from …" summary, and the start date
/// handed to from-template.
public class ProjectCreateModalTemplateTests
{
    private static readonly DateTime Today = new(2026, 10, 12);

    private sealed class Api(List<ProjectCreateModal.TemplateRow> rows) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/projects/templates")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(rows) });
            if (request.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { success = true }) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static readonly ProjectCreateModal.TemplateRow Sprint = new(Guid.NewGuid(), "Software sprint", "Backlog → In progress → Review → Done",
        12, null, 4, 2, 0, IsBuiltIn: true, Icon: "zap", Color: "inf");
    private static readonly ProjectCreateModal.TemplateRow Audit = new(Guid.NewGuid(), "Audit ISO 9001 site visit", null,
        18, null, 6, 3, 4, Visibility: "department", CreatedByName: "Sari Wulandari", DepartmentName: "QA", CanDelete: true);

    private static BunitContext NewContext(Api api)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(new AuthState(http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }

    [Fact]
    public async Task Template_list_groups_rows_and_shows_the_summary_line()
    {
        var api = new Api([Sprint, Audit]);
        using var ctx = NewContext(api);
        ProjectCreateModal.CreateProjectModel? submitted = null;
        var cut = ctx.Render<ProjectCreateModal>(p => p
            .Add(x => x.Today, Today)
            .Add(x => x.OnSubmit, m => submitted = m));

        await cut.InvokeAsync(() => cut.FindAll(".vpm-from button")[1].Click());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".vpm-tpl").Count));

        var heads = cut.FindAll(".vpm-tpl-h").Select(h => h.TextContent).ToList();
        Assert.Equal(["Built-in", "Saved by your team"], heads);
        Assert.Contains("Sari Wulandari · QA · 18 tasks · 6 sections · 3 milestones · used 4×", cut.Markup);
        // Only the team template the caller owns gets ⋯ / Delete.
        Assert.Single(cut.FindAll(".vpm-tpl-more"));

        // The team template is preselected; its summary uses the start date.
        Assert.Equal("Creates 18 tasks in 6 sections · 3 milestones, dates from 12 Oct 2026.", cut.Find(".vpm-incl span").TextContent);

        await cut.InvokeAsync(() => cut.FindAll(".vpm-tpl")[0].Click());
        Assert.Equal("Creates 12 tasks in 4 sections · 2 milestones, dates from 12 Oct 2026.", cut.Find(".vpm-incl span").TextContent);

        await cut.InvokeAsync(() => cut.Find(".proj-modal-foot .btn-sm.accent").Click());
        Assert.NotNull(submitted);
        Assert.Equal(Sprint.Id, submitted!.TemplateId);
        Assert.Equal(new DateTime(2026, 10, 12), submitted.StartDate);
        Assert.Equal(DateTimeKind.Utc, submitted.StartDate!.Value.Kind);
    }

    [Fact]
    public async Task Owner_can_delete_a_team_template_from_the_list()
    {
        var api = new Api([Sprint, Audit]);
        using var ctx = NewContext(api);
        var cut = ctx.Render<ProjectCreateModal>(p => p.Add(x => x.Today, Today));
        await cut.InvokeAsync(() => cut.FindAll(".vpm-from button")[1].Click());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".vpm-tpl").Count));

        await cut.InvokeAsync(() => cut.Find(".vpm-tpl-more").Click());
        await cut.InvokeAsync(() => cut.Find(".vpm-tpl-menu button").Click());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".vpm-tpl")));
        Assert.Contains($"DELETE /api/projects/templates/{Audit.Id}", api.Calls);
        Assert.DoesNotContain("Saved by your team", cut.Markup);
    }
}
