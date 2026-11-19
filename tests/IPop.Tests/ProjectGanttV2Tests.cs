using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Application.Projects;
using IPop.Web.Components.Projects;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Projects v2 stage 3 (web): Gantt date/pixel math, arrow routing and the ProjectGanttView component.
public class ProjectGanttV2Tests
{
    private static readonly DateTime Mon = new(2026, 11, 2);

    // ── math ──
    [Theory]
    [InlineData(GanttZoom.Day, 30)]
    [InlineData(GanttZoom.Week, 13)]
    [InlineData(GanttZoom.Month, 5)]
    public void Zoom_levels_have_the_mockup_pixels_per_day(GanttZoom z, int px) => Assert.Equal(px, GanttMath.PxPerDay(z));

    [Fact]
    public void Origin_is_a_monday_and_today_is_placed_by_day_index()
    {
        var origin = GanttMath.OriginFor(new DateTime(2026, 11, 5)); // Thursday
        Assert.Equal(DayOfWeek.Monday, origin.DayOfWeek);
        Assert.True(origin <= new DateTime(2026, 11, 2));
        Assert.Equal(GanttMath.DayIndex(origin, new DateTime(2026, 11, 15)) * 13, GanttMath.TodayX(origin, new DateTime(2026, 11, 15), 13));
        Assert.Equal(0, GanttMath.TotalDays(origin, origin.AddDays(20)) % 7);
    }

    [Fact]
    public void Weekend_stripes_shade_saturday_and_sunday_of_each_week()
    {
        Assert.Contains("0 65px,var(--pv2g-wknd) 65px 91px", GanttMath.WeekendBackground(13));
        Assert.True(GanttMath.IsWeekend(Mon.AddDays(5)));
        Assert.False(GanttMath.IsWeekend(Mon.AddDays(4)));
    }

    [Fact]
    public void Header_bands_follow_the_zoom()
    {
        var days = GanttMath.SubBands(Mon, 14, GanttZoom.Day);
        Assert.Equal(14, days.Count);
        Assert.Equal("2", days[0].Label);
        var weeks = GanttMath.SubBands(Mon, 14, GanttZoom.Week);
        Assert.Equal(2, weeks.Count);
        Assert.Equal("W45 · 2 Nov", weeks[0].Label);
        Assert.Equal("W45", GanttMath.SubBands(Mon, 14, GanttZoom.Month)[0].Label);
        Assert.Equal("Nov 2026", GanttMath.MonthBands(Mon, 14, GanttZoom.Week)[0].Label);
        Assert.Equal("Nov", GanttMath.MonthBands(Mon, 14, GanttZoom.Month)[0].Label);
    }

    [Fact]
    public void Holidays_merge_over_weekends_into_one_labelled_break()
    {
        var hs = new[] { new GanttHolidayDto(Mon.AddDays(4), "Year-end"), new GanttHolidayDto(Mon.AddDays(7), null), new GanttHolidayDto(Mon.AddDays(12), "Other") };
        var bands = GanttMath.HolidayBands(hs, Mon, 28, GanttZoom.Week);
        Assert.Equal(2, bands.Count);
        Assert.Equal(4, bands[0].Day);
        Assert.Equal(4, bands[0].Length);
        Assert.Contains("Year-end", bands[0].Label);
        Assert.Equal("Other", bands[1].Label);
    }

    [Fact]
    public void Slack_in_working_days_skips_weekends()
    {
        var friday = Mon.AddDays(4);
        Assert.Equal(3, GanttMath.SlackCalendarDays(friday, 1, new HashSet<DateTime>())); // Fri -> Mon
        Assert.Equal(1, GanttMath.SlackCalendarDays(Mon, 1, new HashSet<DateTime>()));
    }

    [Fact]
    public void Fs_arrow_leaves_the_finish_and_enters_the_start()
    {
        var p = new GanttBox(100, 200, 0, false);
        var s = new GanttBox(260, 330, 2, false);
        Assert.Equal("M200 17 H206 V85 H259", GanttMath.ArrowPath("FS", p, s));
    }

    [Fact]
    public void Fs_arrow_detours_when_the_link_runs_backwards()
    {
        var p = new GanttBox(100, 300, 0, false);
        var s = new GanttBox(150, 220, 1, false);
        Assert.Equal("M300 17 H306 V34 H142 V51 H149", GanttMath.ArrowPath("FS", p, s));
        // Successor above the predecessor: the detour goes up.
        var up = GanttMath.ArrowPath("FS", new GanttBox(100, 300, 3, false), new GanttBox(150, 220, 1, false));
        Assert.Equal("M300 119 H306 V102 H142 V51 H149", up);
    }

    [Fact]
    public void Ss_arrow_starts_at_the_predecessor_start_and_ff_ends_at_the_successor_finish()
    {
        var p = new GanttBox(100, 200, 0, false);
        var s = new GanttBox(140, 260, 1, false);
        var ss = GanttMath.ArrowPath("SS", p, s);
        Assert.StartsWith("M100 17", ss);
        Assert.EndsWith("H139", ss);
        var ff = GanttMath.ArrowPath("FF", p, s);
        Assert.StartsWith("M200 17", ff);
        Assert.EndsWith("H261", ff);
        Assert.Contains("H268", ff); // leads 8px past the later finish
    }

    [Fact]
    public void Milestone_ends_use_the_diamond_edge()
    {
        var p = new GanttBox(100, 200, 0, false);
        var m = new GanttBox(300, 300, 1, true);
        Assert.EndsWith("H292", GanttMath.ArrowPath("FS", p, m));
    }

    [Fact]
    public void Constraint_text_is_plain_english()
    {
        Assert.Equal("Start no earlier than 18 Jan", GanttMath.ConstraintText("snet", new DateTime(2027, 1, 18)));
        Assert.Equal("", GanttMath.ConstraintText(null, null));
    }

    // ── layout ──
    private static readonly Guid G1 = Guid.NewGuid(), G2 = Guid.NewGuid(), A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), M1 = Guid.NewGuid();

    private static GanttRowDto Row(Guid id, Guid g, string wbs, string title, DateTime? s, DateTime? e, bool crit = false, IReadOnlyList<GanttLinkDto>? deps = null, int? slack = null) =>
        new(id, g, null, wbs, title, "Working on it", null, "doing", "normal", s, e, 3, 40,
            [new GanttAssigneeDto(Guid.NewGuid(), "Andi Wijaya", "0002")], crit, slack, null, null, null, null, null, null, deps ?? []);

    private static GanttV2Dto Dto(bool withDates = true) => new(
        new GanttV2ProjectDto(Guid.NewGuid(), "ERP", null, null), new GanttWindowDto(Mon, Mon.AddDays(20)),
        [new GanttGroupDto(G1, "Plan", "#2e9d6b", 0), new GanttGroupDto(G2, "Build", "#2f6fed", 1)],
        withDates
            ? [Row(A, G1, "1.1", "Kick-off", Mon, Mon.AddDays(2), true), Row(B, G1, "1.2", "Design", Mon.AddDays(3), Mon.AddDays(8), true, [new GanttLinkDto(A, "FS", 0)]),
               Row(C, G2, "2.1", "Build it", Mon.AddDays(9), Mon.AddDays(15), false, [new GanttLinkDto(B, "SS", 1)], slack: 2)]
            : [Row(A, G1, "1.1", "Kick-off", null, null)],
        [new GanttMilestoneDto(M1, "Go-live", Mon.AddDays(16), null, [C], true)], [], [], [A, B], Mon.AddDays(16), [], null, null);

    [Fact]
    public void Layout_lists_groups_tasks_and_a_milestones_section()
    {
        var l = GanttLayout.Build(Dto());
        Assert.Equal(new[] { "Plan", "Kick-off", "Design", "Build", "Build it", "Milestones", "Go-live" }, l.Lines.Select(x => x.Title).ToArray());
        Assert.Equal(7, l.VisibleRows);
        Assert.True(l.HasDates);
    }

    [Fact]
    public void Collapsing_a_group_pulls_its_rows_onto_the_group_line()
    {
        var l = GanttLayout.Build(Dto(), collapsed: new HashSet<Guid> { G1 });
        Assert.Equal(5, l.VisibleRows);
        Assert.All(l.Lines.Where(x => x.GroupId == G1 && x.Kind == GanttLineKind.Task), x => { Assert.True(x.Hidden); Assert.Equal(0, x.Y); });
    }

    // ── component ──
    private sealed class Api(GanttV2Dto dto) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            if (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/gantt/v2"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(dto) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });
        }
    }

    private static BunitContext Ctx(GanttV2Dto dto)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new Api(dto)) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(new AuthState(http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(http);
        return ctx;
    }

    private static IRenderedComponent<ProjectGanttView> Render(BunitContext ctx, bool ro = false, bool manage = true, string? search = null) =>
        ctx.Render<ProjectGanttView>(p => p.Add(x => x.ProjectId, Guid.NewGuid()).Add(x => x.ReadOnly, ro).Add(x => x.CanManage, manage)
            .Add(x => x.SearchQuery, search).Add(x => x.Today, Mon.AddDays(3)));

    [Fact]
    public void Renders_rows_groups_bars_and_milestones_from_the_dto()
    {
        using var ctx = Ctx(Dto());
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2g-bar")));
        Assert.Equal(3, cut.FindAll(".pv2g-row.g").Count);
        Assert.Equal(3, cut.FindAll(".pv2g-bar").Count);
        Assert.Single(cut.FindAll(".pv2g-ms"));
        Assert.Contains("Go-live", cut.Markup);
        Assert.Equal(3, cut.FindAll(".pv2g-arrows path[marker-end]").Count); // A>B, B>C (SS) and C>milestone
        Assert.Contains("Andi", cut.Find(".pv2g-glabel").TextContent);
    }

    [Fact]
    public void Critical_toggle_changes_the_bar_class_and_wbs_toggle_the_column()
    {
        using var ctx = Ctx(Dto());
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2g-bar")));
        Assert.Equal(2, cut.FindAll(".pv2g-bar.crit").Count);
        Assert.Contains("WBS", cut.Find(".pv2g-row.h").TextContent);
        cut.FindAll(".pv2g-tog").First(b => b.TextContent.Contains("Critical")).Click();
        Assert.Empty(cut.FindAll(".pv2g-bar.crit"));
        cut.FindAll(".pv2g-tog").First(b => b.TextContent.Contains("WBS")).Click();
        Assert.DoesNotContain("WBS", cut.Find(".pv2g-row.h").TextContent);
    }

    [Fact]
    public void Baseline_and_slack_toggles_hide_their_layers()
    {
        using var ctx = Ctx(Dto());
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2g-bar")));
        Assert.Single(cut.FindAll(".pv2g-slack:not(.off)"));
        cut.FindAll(".pv2g-tog").First(b => b.TextContent.Contains("Slack")).Click();
        Assert.Empty(cut.FindAll(".pv2g-slack:not(.off)"));
    }

    [Fact]
    public void Collapsing_a_group_hides_its_bars()
    {
        using var ctx = Ctx(Dto());
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2g-bar")));
        Assert.Empty(cut.FindAll(".pv2g-bar.hid"));
        cut.Find(".pv2g-row.g").Click();
        Assert.Equal(2, cut.FindAll(".pv2g-bar.hid").Count);
        Assert.Contains("col", cut.Find(".pv2g-row.g").ClassList);
    }

    [Fact]
    public void Read_only_has_no_resize_or_link_handles_and_viewers_cannot_baseline_or_import()
    {
        using var ctx = Ctx(Dto());
        var cut = Render(ctx, ro: true, manage: false);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2g-bar")));
        Assert.Empty(cut.FindAll(".pv2g-rs"));
        Assert.Empty(cut.FindAll(".pv2g-hand"));
        Assert.DoesNotContain("Set baseline", cut.Markup);
        Assert.DoesNotContain("Import", cut.Markup);
        Assert.Equal("1", cut.Find(".pv2g").GetAttribute("data-ro"));
    }

    [Fact]
    public void Editors_get_handles_and_the_page_search_filters_rows()
    {
        using var ctx = Ctx(Dto());
        var cut = Render(ctx, search: "design");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2g-bar")));
        Assert.Single(cut.FindAll(".pv2g-bar"));
        Assert.Single(cut.FindAll(".pv2g-rs"));
        Assert.Single(cut.FindAll(".pv2g-hand"));
    }

    [Fact]
    public void Empty_state_when_nothing_is_dated()
    {
        using var ctx = Ctx(Dto(false) with { Milestones = [] });
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.Contains("Add dates to your tasks to see them here", cut.Markup));
        Assert.Contains("Open Table", cut.Markup);
    }

    [Fact]
    public void Legend_matches_the_mockup()
    {
        using var ctx = Ctx(Dto());
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pv2g-legend")));
        var t = cut.Find(".pv2g-legend").TextContent;
        foreach (var s in new[] { "Critical path: a delay here delays go-live", "Baseline", "Slack: days a task can slip", "Milestone", "Deadline", "Date constraint", "Drag a bar" })
            Assert.Contains(s, t);
    }

    // ── tab mapping ──
    [Fact]
    public void Gantt_and_timeline_keys_open_different_screens_and_old_saved_views_still_open_timeline()
    {
        Assert.Equal(7, ProjectViewCatalog.TabIndex("gantt"));
        Assert.Equal(2, ProjectViewCatalog.TabIndex("timeline"));
        Assert.Contains("gantt", ProjectViewCatalog.DefaultTabs);
        // Saved views store the page tab number: 2 still resolves to the Timeline key.
        Assert.Equal("timeline", ProjectViewCatalog.Views.Keys.First(k => ProjectViewCatalog.TabIndex(k) == 2));
    }
}
