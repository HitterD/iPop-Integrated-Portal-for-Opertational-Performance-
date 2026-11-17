using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Application.Projects;
using IPop.Web.Components.Projects;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Projects v2 stage 1 (web B): view tabs from Project.Tabs, the + View picker, health chip, full screen button.
public class ProjectV2HeaderTabsTests
{
    private sealed class RecordingApi : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path, string Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath.EndsWith("/tabs"))
            {
                var doc = System.Text.Json.JsonDocument.Parse(body);
                var tabs = doc.RootElement.GetProperty("tabs").EnumerateArray().Select(x => x.GetString()!).ToList();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(tabs) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
        }
    }

    private static (BunitContext Ctx, RecordingApi Api, ToastService Toasts) NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new RecordingApi();
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        var toasts = new ToastService();
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(new PeopleLinks());
        ctx.Services.AddSingleton<CalendarLinks>();
        ctx.Services.AddScoped<PhotoViewerState>();
        return (ctx, api, toasts);
    }

    private static ProjectDto Proj(IReadOnlyList<string>? tabs = null, string? health = null) =>
        new(Guid.NewGuid(), "ERP Rollout", null, 2026, Guid.NewGuid(), "Owner", "0001", null, DateTime.Today.AddDays(10),
            "active", 40, null, null, true, 3, 10, 2, 2, 2, 4, DateTime.UtcNow, DateTime.UtcNow, null, health, tabs);

    private static string[] TabKeys(IRenderedComponent<ProjectViewTabs> cut) =>
        cut.FindAll(".pv2-vtabs [role=tab]").Select(t => t.GetAttribute("data-view")!).ToArray();

    [Fact]
    public void Default_tabs_are_shown_when_the_project_has_none()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var cut = ctx.Render<ProjectViewTabs>(p => p.Add(x => x.Project, Proj()).Add(x => x.ActiveKey, "table"));
        Assert.Equal(new[] { "overview", "table", "board", "gantt", "workload", "calendar", "reports" }, TabKeys(cut));
        Assert.Contains("on", cut.Find("[data-view=table]").ClassList);
        Assert.Equal("Gantt", cut.Find("[data-view=gantt]").TextContent.Trim());
    }

    [Fact]
    public void Tabs_come_from_Project_Tabs_in_order()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var cut = ctx.Render<ProjectViewTabs>(p => p.Add(x => x.Project, Proj(["board", "files", "doc"])));
        Assert.Equal(new[] { "board", "files", "doc" }, TabKeys(cut));
    }

    [Fact]
    public void Picker_lists_views_marks_added_and_filters_on_search()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var cut = ctx.Render<ProjectViewTabs>(p => p.Add(x => x.Project, Proj(["table", "board"])).Add(x => x.CanManage, true));
        cut.Find(".pv2-vt.add").Click();
        Assert.Equal(16, cut.FindAll(".pv2-pick .pv2-vo").Count);
        Assert.Contains("added", cut.Find(".pv2-pick [data-view=table]").ClassList);
        Assert.Contains("Added", cut.Find(".pv2-pick [data-view=table]").TextContent);
        Assert.DoesNotContain("added", cut.Find(".pv2-pick [data-view=gantt]").ClassList);

        cut.Find("#pv2-pick-q").Input("gantt");
        var shown = cut.FindAll(".pv2-pick .pv2-vo").Select(x => x.GetAttribute("data-view")).ToArray();
        Assert.Equal(new[] { "gantt" }, shown);
    }

    [Fact]
    public void Adding_a_view_puts_the_new_tab_list_selects_it_and_offers_undo()
    {
        var (ctx, api, toasts) = NewContext();
        using var _c = ctx;
        string? selected = null; IReadOnlyList<string>? saved = null;
        var cut = ctx.Render<ProjectViewTabs>(p => p.Add(x => x.Project, Proj(["table"])).Add(x => x.CanManage, true)
            .Add(x => x.OnSelect, (string k) => selected = k).Add(x => x.OnTabsSaved, (IReadOnlyList<string> t) => saved = t));
        cut.Find(".pv2-vt.add").Click();
        cut.Find(".pv2-pick [data-view=whiteboard]").Click();

        var put = Assert.Single(api.Calls, c => c.Method == HttpMethod.Put);
        Assert.EndsWith("/tabs", put.Path);
        Assert.Contains("\"tabs\":[\"table\",\"whiteboard\"]", put.Body);
        Assert.Equal("whiteboard", selected);
        Assert.Equal(new[] { "table", "whiteboard" }, saved);
        Assert.Contains(toasts.Active, t => t.Message.Contains("Whiteboard") && t.Undo is not null);
        Assert.Empty(cut.FindAll(".pv2-pick"));
    }

    [Fact]
    public void Picking_a_view_that_is_already_there_only_switches_to_it()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        string? selected = null;
        var cut = ctx.Render<ProjectViewTabs>(p => p.Add(x => x.Project, Proj(["table", "board"])).Add(x => x.CanManage, true)
            .Add(x => x.OnSelect, (string k) => selected = k));
        cut.Find(".pv2-vt.add").Click();
        cut.Find(".pv2-pick [data-view=board]").Click();
        Assert.Equal("board", selected);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Non_managers_see_a_note_and_cannot_add_views()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var cut = ctx.Render<ProjectViewTabs>(p => p.Add(x => x.Project, Proj(["table"])).Add(x => x.CanManage, false));
        cut.Find(".pv2-vt.add").Click();
        Assert.Contains("Only the project owner or a manager can add views", cut.Find(".pv2-pick-note").TextContent);
        Assert.True(cut.Find(".pv2-pick [data-view=gantt]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".pv2-vx"));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Remove_cross_puts_the_list_without_it_and_the_last_tab_cannot_be_removed()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var cut = ctx.Render<ProjectViewTabs>(p => p.Add(x => x.Project, Proj(["table", "board"])).Add(x => x.CanManage, true));
        Assert.Equal(2, cut.FindAll(".pv2-vx").Count);
        cut.FindAll(".pv2-vx")[1].Click();
        Assert.Contains("\"tabs\":[\"table\"]", Assert.Single(api.Calls, c => c.Method == HttpMethod.Put).Body);

        var one = ctx.Render<ProjectViewTabs>(p => p.Add(x => x.Project, Proj(["table"])).Add(x => x.CanManage, true));
        Assert.Empty(one.FindAll(".pv2-vx"));
    }

    [Fact]
    public void Health_chip_cycles_and_saves_for_a_manager()
    {
        var (ctx, api, toasts) = NewContext();
        using var _c = ctx;
        string? changed = null;
        var cut = ctx.Render<ProjectHeader>(p => p.Add(x => x.Project, Proj(health: "on_track")).Add(x => x.V2, true).Add(x => x.CanManage, true)
            .Add(x => x.OnHealthChanged, (string h) => changed = h));
        Assert.Contains("On track", cut.Find(".pv2-health").TextContent);
        cut.Find("button.pv2-health").Click();
        var put = Assert.Single(api.Calls, c => c.Method == HttpMethod.Put);
        Assert.Contains("\"health\":\"at_risk\"", put.Body);
        Assert.Equal("at_risk", changed);
        Assert.Contains("At risk", cut.Find(".pv2-health").TextContent);
        Assert.Contains(toasts.Active, t => t.Message == "Health set to At risk.");
    }

    [Fact]
    public void Health_chip_is_read_only_for_others()
    {
        var (ctx, api, _) = NewContext();
        using var _c = ctx;
        var cut = ctx.Render<ProjectHeader>(p => p.Add(x => x.Project, Proj(health: "off_track")).Add(x => x.V2, true).Add(x => x.CanManage, false));
        Assert.Empty(cut.FindAll("button.pv2-health"));
        Assert.Contains("Off track", cut.Find("span.pv2-health.ro").TextContent);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Header_keeps_the_menu_items_and_shows_progress_and_late_count()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var cut = ctx.Render<ProjectHeader>(p => p.Add(x => x.Project, Proj()).Add(x => x.V2, true).Add(x => x.LateCount, 3));
        Assert.Contains("40% done", cut.Find(".pv2-progress").TextContent);
        Assert.Contains("4 of 10 tasks", cut.Find(".pv2-progress").TextContent);
        Assert.Contains("3 late", cut.Find(".pv2-late").TextContent);
        cut.Find("button[aria-haspopup=menu]").Click();
        var menu = cut.Find(".ph-menu").TextContent;
        foreach (var item in new[] { "Statuses", "Edit project details", "Export to Excel", "Copy link", "Save as template", "Mark project completed", "Delete project", "Schedule team meeting", "Project status" })
            Assert.Contains(item, menu);
    }

    [Fact]
    public void Full_screen_button_flips_label_and_key_hint()
    {
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var on = false;
        var cut = ctx.Render<ProjectCrumbBar>(p => p.Add(x => x.FullScreen, false).Add(x => x.OnToggleFullScreen, () => on = !on));
        Assert.Contains("Full screen", cut.Find(".pv2-fsbtn").TextContent);
        Assert.Equal("F", cut.Find(".pv2-fsbtn kbd").TextContent);
        Assert.Empty(cut.FindAll(".pv2-railbtn"));
        cut.Find(".pv2-fsbtn").Click();
        Assert.True(on);

        cut.Render(p => p.Add(x => x.FullScreen, true));
        Assert.Contains("Exit full screen", cut.Find(".pv2-fsbtn").TextContent);
        Assert.Equal("Esc", cut.Find(".pv2-fsbtn kbd").TextContent);
        Assert.Single(cut.FindAll(".pv2-railbtn"));
    }

    [Fact]
    public void Tabs_without_a_screen_map_to_placeholders()
    {
        Assert.Equal(-1, ProjectViewCatalog.TabIndex("whiteboard"));
        Assert.Equal(2, ProjectViewCatalog.TabIndex("gantt"));
        Assert.Equal(1, ProjectViewCatalog.TabIndex("table"));
        var (ctx, _, _) = NewContext();
        using var _c = ctx;
        var cut = ctx.Render<ProjectViewPlaceholder>(p => p.Add(x => x.Key, "form"));
        Assert.Contains("Requests become tasks", cut.Markup);
    }
}
