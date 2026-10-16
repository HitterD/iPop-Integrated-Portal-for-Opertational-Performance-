using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Application.Shell;
using IPop.Web.Components.Phone;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Phone tab bar (mockup v1 frames g, h): Chat · Mail · Calls · Calendar + More by default (stage 1 had
/// Dashboard · Chat · Mail · Calendar · Projects), user-editable, saved to the account.
public class MainLayoutTabsTests
{
    private static readonly string Web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "IPop.Web"));

    private sealed class Api(bool admin = false) : HttpMessageHandler
    {
        public string[]? Saved;
        public string? NotifCard;
        public List<(string Method, string Path, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            Calls.Add((req.Method.Method, path, body));
            if (path == "/api/directory/me")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DirectoryEmployeeDto("T1", "Bagastyo Gunawan", null, "ICT", null, null, null, null, "bagastyo@santos.co.id", null, null, "on", admin ? ["Admin"] : []) ) };
            if (path == "/api/me/ui-prefs/tabs" && req.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(PhoneTabs.Resolve(Saved, admin)) };
            if (path == "/api/me/ui-prefs/tabs" && req.Method == HttpMethod.Put)
            {
                Saved = System.Text.Json.JsonSerializer.Deserialize<string[]>(body);
                return new(HttpStatusCode.NoContent);
            }
            if (path == "/api/me/ui-prefs/notif-card")
            {
                if (req.Method == HttpMethod.Put) { NotifCard = body; return new(HttpStatusCode.NoContent); }
                return NotifCard is null ? new(HttpStatusCode.NoContent) : new(HttpStatusCode.OK) { Content = new StringContent(NotifCard) };
            }
            return new(HttpStatusCode.NotFound);
        }
    }

    private static (BunitContext Ctx, Api Api) Context(bool admin = false, string[]? saved = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new Api(admin) { Saved = saved };
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        auth.LoadMeAsync().GetAwaiter().GetResult();
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new BadgeState(auth));
        ctx.Services.AddSingleton(new OverlayStack());
        ctx.Services.AddSingleton(new TabPrefsState(auth));
        ctx.Services.AddSingleton(new PhoneState(ctx.JSInterop.JSRuntime));
        return (ctx, api);
    }

    private static string[] TabLabels(IRenderedComponent<TabBar> cut)
        => cut.FindAll(".m-tab .m-tab-lbl").Select(e => e.TextContent.Trim()).ToArray();

    [Fact]
    public void Default_tabs_are_chat_mail_calls_calendar_and_more()
    {
        var (ctx, _) = Context();
        using var _c = ctx;
        var cut = ctx.Render<TabBar>();
        Assert.Equal(["Chat", "Mail", "Calls", "Calendar", "More"], TabLabels(cut));
        Assert.Equal(["/chat", "/mail", "/calls", "/calendar"], cut.FindAll("a.m-tab").Select(a => a.GetAttribute("href")));
    }

    [Fact]
    public async Task Saved_prefs_reorder_the_bar()
    {
        var (ctx, _) = Context(saved: ["/notes", "/chat", "/mail", "/calendar"]);
        using var _c = ctx;
        await ctx.Services.GetRequiredService<TabPrefsState>().LoadAsync();
        var cut = ctx.Render<TabBar>();
        Assert.Equal(["Notes", "Chat", "Mail", "Calendar", "More"], TabLabels(cut));
    }

    [Fact]
    public void Badges_carry_over_and_More_counts_only_unpinned_modules()
    {
        var (ctx, _) = Context();
        using var _c = ctx;
        var badges = ctx.Services.GetRequiredService<BadgeState>();
        badges.Set("chat", 3);
        var cut = ctx.Render<TabBar>();
        Assert.Equal("3", cut.Find("a.m-tab[href='/chat'] .m-tab-badge").TextContent);
        Assert.Empty(cut.FindAll("button.m-tab .m-tab-badge"));     // chat is pinned: no More dot
        Assert.Equal(0, TabBar.MoreBadgeTotal(PhoneTabs.Default, badges, false));
        cut.InvokeAsync(() => badges.Set("tickets", 2));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.m-tab .m-tab-badge.dot")));
        Assert.Equal(2, TabBar.MoreBadgeTotal(PhoneTabs.Default, badges, false));
    }

    [Fact]
    public void More_sheet_lists_every_unpinned_module_and_admin_only_for_admins()
    {
        var (ctx, _) = Context();
        using var _c = ctx;
        var cut = ctx.Render<MoreSheet>(p => p.Add(x => x.Open, true));
        var tiles = cut.FindAll(".ph-mgrid .ph-mt").Select(e => e.GetAttribute("href")).ToArray();
        Assert.Equal(["/dashboard", "/directory", "/notes", "/projects", "/tickets", "/news", "/notifications", "/files"], tiles);
        Assert.Contains("Edit tabs", cut.Markup);
        Assert.Contains("4 pinned", cut.Find(".ph-more-edit").TextContent);
        Assert.Contains("Sign out", cut.Markup);
        Assert.Contains("bagastyo@santos.co.id", cut.Find(".ph-prof").TextContent);

        var (actx, _) = Context(admin: true);
        using var _a = actx;
        var admin = actx.Render<MoreSheet>(p => p.Add(x => x.Open, true));
        var adminTile = admin.Find(".ph-mt[href='/admin']");
        Assert.Contains("ADMIN", adminTile.TextContent);
    }

    [Fact]
    public void More_sheet_never_shows_the_employee_number()
    {
        var (ctx, _) = Context();
        using var _c = ctx;
        var cut = ctx.Render<MoreSheet>(p => p.Add(x => x.Open, true));
        Assert.DoesNotContain("T1", cut.Find(".ph-prof").TextContent);
    }

    [Fact]
    public async Task Edit_tabs_pins_unpins_reorders_and_saves_to_the_account()
    {
        var (ctx, api) = Context();
        using var _c = ctx;
        var prefs = ctx.Services.GetRequiredService<TabPrefsState>();
        var cut = ctx.Render<EditTabsSheet>(p => p.Add(x => x.Open, true));
        Assert.Equal(["Chat", "Mail", "Calls", "Calendar", "More"], cut.FindAll(".ph-tprev > div").Select(d => d.TextContent.Trim()));
        Assert.Contains("Pinned · 4 of 4", cut.Markup);

        cut.Find("button[aria-label='Unpin Calls & Meetings']").Click();
        Assert.Contains("Pinned · 3 of 4", cut.Markup);
        Assert.True(cut.Find(".ph-done").HasAttribute("disabled"));     // need exactly four
        cut.Find("button[aria-label='Pin Notes']").Click();
        cut.Find("button[aria-label='Move Notes up']").Click();          // Chat Mail Notes Calendar
        Assert.Equal(["Chat", "Mail", "Notes", "Calendar", "More"], cut.FindAll(".ph-tprev > div").Select(d => d.TextContent.Trim()));

        await cut.InvokeAsync(() => cut.Find(".ph-done").Click());
        cut.WaitForAssertion(() => Assert.Equal(["/chat", "/mail", "/notes", "/calendar"], prefs.Tabs));
        Assert.Contains(api.Calls, c => c.Method == "PUT" && c.Path == "/api/me/ui-prefs/tabs" && c.Body == "[\"/chat\",\"/mail\",\"/notes\",\"/calendar\"]");
        // The local mirror (per account) lets the next start render the bar before the API answers.
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "ipopUserStore.set" && Equals(i.Arguments[1], "phone-tabs"));
    }

    [Fact]
    public void Edit_tabs_reset_restores_the_default()
    {
        var (ctx, _) = Context(saved: ["/notes", "/chat", "/mail", "/calendar"]);
        using var _c = ctx;
        ctx.Services.GetRequiredService<TabPrefsState>().LoadAsync().GetAwaiter().GetResult();
        var cut = ctx.Render<EditTabsSheet>(p => p.Add(x => x.Open, true));
        cut.Find(".ph-reset").Click();
        Assert.Equal(["Chat", "Mail", "Calls", "Calendar", "More"], cut.FindAll(".ph-tprev > div").Select(d => d.TextContent.Trim()));
    }

    [Fact]
    public void Edit_tabs_offers_admin_settings_only_to_admins()
    {
        var (ctx, _) = Context();
        using var _c = ctx;
        var cut = ctx.Render<EditTabsSheet>(p => p.Add(x => x.Open, true));
        Assert.DoesNotContain("Admin settings", cut.Markup);
    }

    [Fact]
    public void Layout_renders_the_phone_kit_tab_bar_and_sheets()
    {
        var razor = File.ReadAllText(Path.Combine(Web, "Layout", "MainLayout.razor"));
        Assert.Contains("<TabBar", razor);
        Assert.Contains("<MoreSheet", razor);
        Assert.Contains("<EditTabsSheet", razor);
        Assert.DoesNotContain("TabHrefs", razor);   // the stage-1 hard-coded list is gone
    }
}
