using Bunit;
using IPop.Web.Components.Phone.News;
using IPop.Web.Models;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace IPop.Tests;
public class PhoneNewsTests
{
    private static NewsSummaryDto Summary(string title, bool pinned = false) => new(Guid.NewGuid(), "news", title, "published", "Jane Smith", "SECRET-NIK", null, DateTime.UtcNow, pinned, null, false, [], false, DateTime.UtcNow, DateTime.UtcNow);
    internal static NewsDetailDto Detail(string type = "news") => new(Guid.NewGuid(), type, "Company update", "Real article body", "published", Guid.NewGuid(), "Jane Smith", "SECRET-NIK", null, null, null, null, DateTime.UtcNow, false, null, false, [], [], false, 0, 0, DateTime.UtcNow, DateTime.UtcNow);
    [Fact]
    public void Feed_picks_pinned_hero_compact_rows_filters_and_checks_compose_permission()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var newest = Summary("Newest"); var pinned = Summary("Pinned", true); Guid? opened = null; string? filter = null;
        var cut = ctx.Render<NewsPhone>(p => p.Add(x => x.Items, new[] { newest, pinned }).Add(x => x.OnOpen, id => opened = id).Add(x => x.OnView, value => filter = value));
        Assert.Contains("Pinned", cut.Find(".m-news-hero").TextContent); Assert.Contains("Newest", cut.Find(".m-news-row").TextContent);
        Assert.DoesNotContain("SECRET-NIK", cut.Markup); Assert.Empty(cut.FindAll("a[href='/news/new']"));
        cut.Find(".m-news-hero").Click(); Assert.Equal(pinned.Id, opened);
        cut.Find("button[data-view=ack]").Click(); Assert.Equal("ack", filter);
        cut.Render(p => p.Add(x => x.CanManage, true)); Assert.NotNull(cut.Find("a[href='/news/new']"));
    }
    [Fact]
    public void Reader_condolence_has_no_cover_or_like_and_private_chat_uses_resolved_url()
    {
        using var ctx = Context();
        var cut = ctx.Render<NewsArticlePhone>(p => p.Add(x => x.Detail, Detail("condolence")).Add(x => x.CondolenceChatUrl, "/chat?with=resolved-token"));
        Assert.NotNull(cut.Find(".m-news-calm")); Assert.Empty(cut.FindAll(".m-news-cover,button[aria-label='Like post']"));
        Assert.Equal("/chat?with=resolved-token", cut.Find("a[data-action=condolence]").GetAttribute("href"));
        Assert.DoesNotContain("SECRET-NIK", cut.Markup);
    }
    [Fact]
    public void Condolence_without_known_colleague_opens_explicit_people_picker_instead_of_guessing_author()
    {
        using var ctx = Context();
        var cut = ctx.Render<NewsArticlePhone>(p => p.Add(x => x.Detail, Detail("condolence")));
        cut.Find("button[data-action=condolence]").Click();
        Assert.NotNull(cut.Find(".ph-sheet [aria-label='Search people']"));
        Assert.DoesNotContain("SECRET-NIK", cut.Markup);
        Assert.Empty(cut.FindAll("a[href^='/chat']"));
    }
    [Fact]
    public void Discussion_failure_exposes_retry_without_fake_zero_reaction_counts()
    {
        using var ctx = Context(); var retried = false;
        var cut = ctx.Render<NewsArticlePhone>(p => p.Add(x => x.Detail, Detail()).Add(x => x.DiscussionError, "Couldn't load comments and reactions.").Add(x => x.OnRetryDiscussion, () => retried = true));
        cut.Find("button[data-action=retry-discussion]").Click(); Assert.True(retried);
        Assert.Empty(cut.FindAll(".m-news-reactions"));
    }
    [Fact]
    public void Reader_actions_dispatch_existing_handlers_and_ack_remains_explicit()
    {
        using var ctx = Context(); var liked = false; var ack = false;
        var cut = ctx.Render<NewsArticlePhone>(p => p.Add(x => x.Detail, Detail() with { RequiresAcknowledgement = true }).Add(x => x.OnLike, () => liked = true).Add(x => x.OnAcknowledge, () => ack = true));
        Assert.Contains("Real article body", cut.Markup);
        cut.Find("button[aria-label='Like post']").Click(); Assert.True(liked); Assert.False(ack);
        cut.Find("button[data-action=acknowledge]").Click(); Assert.True(ack);
    }
    [Fact]
    public void Phone_styles_pin_real_editor_toolbar_above_visual_viewport_and_keep_reading_type_scale()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "IPop.Web"))) dir = Path.GetDirectoryName(dir);
        var css = File.ReadAllText(Path.Combine(dir!, "src/IPop.Web/wwwroot/css/mobile/news.css"));
        Assert.Contains("font-size: 27px", css); Assert.Contains("font-size: 17px", css); Assert.Contains("line-height: 1.6", css);
        Assert.Contains("body.ph-kb-open .m-news-compose .nce-bar", css);
        Assert.Contains("bottom: var(--ph-kb, 0px)", css);
    }
    [Fact]
    public void Phone_composer_opens_real_settings_sheet_with_overlay_and_existing_editor_handlers()
    {
        using var ctx = PageContext(true, false);
        var cut = ctx.Render<IPop.Web.Pages.NewsCompose>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".m-news-compose")));
        Assert.NotNull(cut.Find("[aria-label='Post category']"));
        Assert.Empty(cut.FindAll(".nc-settings-col"));
        cut.Find("button[aria-label='Post settings']").Click();
        Assert.NotNull(cut.Find(".ph-sheet-tall .nc-settings-col"));
        Assert.Single(cut.FindComponents<IPop.Web.Components.OverlayHook>());
        Assert.NotNull(cut.Find(".nce-bar[role=toolbar]"));
    }
    [Fact]
    public void Desktop_composer_keeps_original_settings_and_no_phone_controls()
    {
        using var ctx = PageContext(false, false);
        var cut = ctx.Render<IPop.Web.Pages.NewsCompose>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".nc-shell > .nc-main > .nc-settings-col")));
        Assert.Empty(cut.FindAll(".m-news-compose, .ph-sheet, [aria-label='Post category']"));
    }
    [Fact]
    public void Drafts_failure_exposes_retry_instead_of_claiming_empty_list()
    {
        using var ctx = PageContext(true, true);
        var cut = ctx.Render<IPop.Web.Pages.NewsDrafts>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[role=alert]")));
        Assert.Contains("Retry", cut.Find("[role=alert]").TextContent);
        Assert.DoesNotContain("No drafts yet", cut.Markup);
    }
    private static BunitContext PageContext(bool phone, bool fail)
    {
        var ctx = Context();
        var http = new HttpClient(new NewsHandler(fail)) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(http); ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton<ToastService>(); ctx.Services.AddSingleton<ConfirmService>(); ctx.Services.AddSingleton<PeopleLinks>();
        var state = new PhoneState(ctx.JSInterop.JSRuntime); state.OnPhoneChanged(phone); ctx.Services.AddSingleton(state);
        return ctx;
    }
    private sealed class NewsHandler(bool fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (fail) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            var now = DateTime.UtcNow;
            object body = req.RequestUri!.AbsolutePath.EndsWith("counts") ? new NewsCountsDto(0,0,0,0,0,0,0,true) :
                new IPop.Application.News.NewsDraftResultDto(true, new(Guid.NewGuid(), Guid.NewGuid(), "Jane Smith", null, "", null, 1, "active", new(Type: "news", Title: "Real draft"), now, now), null);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(body) });
        }
    }
    private static BunitContext Context()
    {
        var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(http); ctx.Services.AddSingleton(new AuthState(http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton<OverlayStack>(); return ctx;
    }
}
