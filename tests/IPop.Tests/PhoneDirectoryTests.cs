using Bunit;
using IPop.Web.Components.Phone.Directory;
using IPop.Web.Models;
using IPop.Web.Services;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace IPop.Tests;

public class PhoneDirectoryTests
{
    private static Employee Person(string name = "JANE SMITH") => new(name, "SECRET POSITION", "SECRET-NIK", "1204", "Office", "ICT", "ict", "Sepanjang", "sja", "Mary", "jane@example.invalid", "on", "#123528");

    [Fact]
    public void People_rows_show_title_case_placement_presence_and_dispatch_contact_actions_without_identity_fields()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Employee? opened = null, called = null, chatted = null;
        var cut = ctx.Render<PeoplePhone>(p => p.Add(x => x.People, new[] { Person() })
            .Add(x => x.OnOpen, person => opened = person).Add(x => x.OnCall, person => called = person)
            .Add(x => x.OnChat, person => chatted = person));
        Assert.Contains("Jane Smith", cut.Markup);
        Assert.Contains("ICT · Sepanjang", cut.Markup);
        Assert.NotNull(cut.Find(".m-directory-presence.on"));
        Assert.DoesNotContain("SECRET-NIK", cut.Find(".m-directory-row").TextContent);
        Assert.DoesNotContain("SECRET POSITION", cut.Markup);
        cut.Find("button[aria-label='Chat with Jane Smith']").Click();
        cut.Find("button[aria-label='Call Jane Smith']").Click();
        cut.Find(".m-directory-person").Click();
        Assert.Equal(Person(), opened);
        Assert.Equal(Person(), called);
        Assert.Equal(Person(), chatted);
        Assert.NotNull(cut.Find("a[aria-label='Jump to J']"));
    }

    [Fact]
    public void Search_highlights_without_injecting_html_and_suggests_only_loaded_names()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddScoped<PhotoViewerState>();
        var cut = ctx.Render<PeoplePhone>(p => p.Add(x => x.People, new[] { Person("<script>Jane</script>") }).Add(x => x.Query, "Jane"));
        Assert.Equal("Jane", cut.Find("mark").TextContent);
        Assert.Empty(cut.FindAll("script"));
        Assert.Equal("Dimas Pratama", DirectorySuggest.Find("dimas prtama", new[] { "Dimas Pratama", "Mary Smith" }));
        Assert.Null(DirectorySuggest.Find("zzzzzz", new[] { "Jane Smith" }));
        Assert.Null(DirectorySuggest.Find("", new[] { "Jane Smith" }));
    }

    [Fact]
    public void People_selection_is_separate_from_profile_and_exposes_string_aria_checked()
    {
        using var ctx = new BunitContext(); ctx.Services.AddScoped<PhotoViewerState>(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Employee? chosen = null;
        var cut = ctx.Render<PeoplePhone>(p => p.Add(x => x.People, new[] { Person() }).Add(x => x.Selecting, true).Add(x => x.Selected, new HashSet<string> { Person().Nik }).Add(x => x.OnSelect, value => chosen = value));
        Assert.Equal("true", cut.Find("button[role=checkbox]").GetAttribute("aria-checked"));
        cut.Find("button[role=checkbox]").Click(); Assert.Equal(Person(), chosen);
    }

    [Fact]
    public void Person_detail_uses_real_contact_and_permission_checked_calendar_links_without_private_identity()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var favorite = false;
        var cut = ctx.Render<PersonPhone>(p => p.Add(x => x.Person, Person()).Add(x => x.CalendarUrl, "/calendar/person/token")
            .Add(x => x.MeetingUrl, "/calendar?new=1").Add(x => x.OnFavorite, () => favorite = true));
        Assert.DoesNotContain("SECRET POSITION", cut.Markup);
        Assert.DoesNotContain("SECRET-NIK", cut.Find(".ph-scroll").TextContent);
        Assert.Equal("/calendar/person/token", cut.Find("a[aria-label='View calendar']").GetAttribute("href"));
        Assert.Contains("Reports to", cut.Markup);
        Assert.Contains("Mary", cut.Markup);
        Assert.DoesNotContain("Office phone", cut.Markup); // No office phone exists in the API contract.
        cut.Find("button[aria-label='Favorite']").Click();
        Assert.True(favorite);
    }

    [Fact]
    public void Departments_keep_site_occurrences_and_expand_separately_from_selection()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddScoped<PhotoViewerState>();
        var nav = new DirectoryNavigation();
        nav.Nodes.AddRange(new[] {
            new DirectoryNavNode("s", "site", "s", "s", "Sepanjang", 12, 0, null),
            new DirectoryNavNode("s/d", "dept", "d", "s", "ICT", 12, 1, "s"),
            new DirectoryNavNode("s/d/u", "div", "u", "s", "Network", 5, 2, "s/d") });
        nav.Expanded.Add("s");
        DirectoryNavNode? selected = null;
        var cut = ctx.Render<DepartmentsPhone>(p => p.Add(x => x.Navigation, nav)
            .Add(x => x.OnSelect, node => selected = node)
            .Add(x => x.OnToggle, node => { if (!nav.Expanded.Add(node.Key)) nav.Expanded.Remove(node.Key); }));
        Assert.Contains("Sepanjang", cut.Markup);
        Assert.Empty(cut.FindAll("button[data-node='s/d/u']"));
        cut.Find("button[aria-label='Expand ICT']").Click();
        Assert.Null(selected);
        cut.Find("button[data-node='s/d/u']").Click();
        Assert.Equal("Network", selected?.Name);
    }
}
