using Bunit;
using IPop.Infrastructure.Dashboard;
using Xunit;

namespace IPop.Tests;

/// Mobile Stage 1 hotfix: rules that markup and services must keep (see .scratch/mobile-fixes).
public class MobileHotfixTests
{
    private static readonly string Web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "IPop.Web"));
    private static string Read(string rel) => File.ReadAllText(Path.Combine(Web, rel));

    // ── #15 Dashboard "New post" only for people who may post ──

    [Fact]
    public async Task Dashboard_says_an_employee_cannot_post_news()
    {
        using var t = new TestDb();
        var me = t.AddUser("MOB_EMP");
        var dto = await new DashboardService(t.Verify()).GetAsync(me.Id);
        Assert.NotNull(dto);
        Assert.False(dto!.CanPostNews);
    }

    [Fact]
    public async Task Dashboard_says_hr_and_admin_can_post_news()
    {
        using var t = new TestDb();
        var hr = t.AddHr("MOB_HR");
        var admin = t.AddAdmin("MOB_ADM");
        Assert.True((await new DashboardService(t.Verify()).GetAsync(hr.Id))!.CanPostNews);
        Assert.True((await new DashboardService(t.Verify()).GetAsync(admin.Id))!.CanPostNews);
    }

    [Fact]
    public void Dashboard_quick_action_new_post_is_gated_on_CanPostNews()
    {
        var razor = Read("Pages/Dashboard.razor");
        var line = razor.Split('\n').First(l => l.Contains("class=\"m-news\""));
        Assert.Contains("CanPostNews", line);
    }

    // ── #11 Notification banner wording ──

    [Theory]
    [InlineData(false, true, "Turn on Windows notifications")]
    [InlineData(true, true, "Turn on notifications")]
    [InlineData(false, false, "Turn on notifications")]
    [InlineData(true, false, "Turn on notifications")]
    public void Push_banner_names_Windows_only_on_a_Windows_desktop(bool phone, bool windows, string expected)
        => Assert.Equal(expected, IPop.Web.Layout.MainLayout.PushBannerTitle(phone, windows));

    // ── #12 Icon-only Notes buttons carry an accessible name ──

    [Fact]
    public void Notes_icon_only_buttons_have_aria_labels()
    {
        var src = Read("Pages/Notes.razor");
        // <button ... class="nt-icon-btn ..." ...><i data-lucide=...></i></button> with no text must have aria-label.
        var re = new System.Text.RegularExpressions.Regex(@"<button\b(?:[^>""]|""[^""]*"")*>\s*(<i [^>]*></i>\s*)+</button>");
        var all = re.Matches(src).Select(m => m.Value).ToList();
        Assert.True(all.Count >= 15, $"expected the icon-only buttons to be found, got {all.Count}");
        var missing = all.Where(b => !b.Contains("aria-label")).ToList();
        Assert.Empty(missing);
        // Row ⋯ / + spans are buttons for screen readers too.
        var spans = new System.Text.RegularExpressions.Regex(@"<span (?:role=""button"" tabindex=""-1"" )?title=""(More|Add sub-page)""(?:[^>""]|""[^""]*"")*>").Matches(src);
        Assert.NotEmpty(spans);
        Assert.All(spans, m => Assert.Contains("aria-label", m.Value));
    }

    // ── #8 Dark headings: Radzen title colour follows the app ink globally ──

    [Fact]
    public void Dark_theme_maps_radzen_title_colour_to_ink()
    {
        var css = Read("wwwroot/css/components/third-party-overrides.css");
        var dark = css[css.IndexOf("/* Dark theme Radzen mapping */", StringComparison.Ordinal)..];
        dark = dark[..dark.IndexOf('}')];
        Assert.Contains("[data-theme=\"dark\"] {", dark);
        Assert.Contains("--rz-text-title-color: var(--ink)", dark);
    }

    // ── #13 News media URLs resolve against the API host ──

    [Theory]
    [InlineData("/api/news/composer/media/abc?t=1", "http://api.local:5995/", "http://api.local:5995/api/news/composer/media/abc?t=1")]
    [InlineData("https://cdn.example/x.png", "http://api.local:5995/", "https://cdn.example/x.png")]
    [InlineData("/api/x", null, "/api/x")]
    public void News_media_url_is_resolved_against_the_api_base(string url, string? apiBase, string expected)
        => Assert.Equal(expected, IPop.Web.Components.News.NewsBody.ResolveMediaUrl(url, apiBase is null ? null : new Uri(apiBase)));
}
