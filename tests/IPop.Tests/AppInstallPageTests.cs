using System.Net;
using Bunit;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// /app install page (mockup v1 frames p1, p2, p3): works signed out, picks the platform from the UA.
public class AppInstallPageTests
{
    private static readonly string Web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "IPop.Web"));

    private static BunitContext Ctx(string platform, bool apk)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<string>("ipop.phone.platform").SetResult(platform);
        ctx.JSInterop.Setup<long>("ipop.phone.size", _ => true).SetResult(apk ? 2_358_126 : 0);
        ctx.JSInterop.Setup<bool>("ipop.phone.iosSafari").SetResult(true);
        ctx.JSInterop.Setup<string>("ipop.phone.installState").SetResult("unsupported");
        ctx.JSInterop.Setup<string>("ipop.phone.qrSvg", _ => true).SetResult("<svg class=\"qr\"></svg>");
        ctx.Services.AddSingleton(new OverlayStack());
        return ctx;
    }

    [Fact]
    public void Android_shows_the_apk_first_then_chrome_steps()
    {
        using var ctx = Ctx("android", apk: true);
        var cut = ctx.Render<AppInstall>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("a.ph-dl")));
        var dl = cut.Find("a.ph-dl");
        Assert.Equal("/download/ipop.apk", dl.GetAttribute("href"));
        Assert.Contains("Download for Android", dl.TextContent);
        Assert.Contains("2.2 MB", dl.TextContent);
        Assert.Equal("true", cut.Find("[role=tab][data-value=and]").GetAttribute("aria-selected"));
        Assert.Contains("Choose <b>Install app</b>", cut.Markup);
        Assert.Empty(cut.FindAll(".ph-qr"));
    }

    [Fact]
    public void Android_without_an_apk_says_coming_soon_and_keeps_chrome_install()
    {
        using var ctx = Ctx("android", apk: false);
        var cut = ctx.Render<AppInstall>();
        cut.WaitForAssertion(() => Assert.Contains("coming soon", cut.Markup));
        Assert.Empty(cut.FindAll("a.ph-dl"));
        Assert.Contains("Tap the <b>⋮</b> menu", cut.Markup);
    }

    [Fact]
    public void IPhone_shows_safari_add_to_home_screen_steps()
    {
        using var ctx = Ctx("ios", apk: true);
        var cut = ctx.Render<AppInstall>();
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[role=tab][data-value=ios]").GetAttribute("aria-selected")));
        Assert.Contains("Add to Home Screen", cut.Markup);
        Assert.Contains("iPhone can't install APK files", cut.Markup);
        Assert.Empty(cut.FindAll("a.ph-dl"));
    }

    [Fact]
    public void Desktop_gets_a_qr_code_of_the_app_page_and_both_instructions_collapsed()
    {
        using var ctx = Ctx("desktop", apk: true);
        var cut = ctx.Render<AppInstall>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".ph-qr svg")));
        Assert.Contains("Install iPop on your phone", cut.Markup);
        var qr = ctx.JSInterop.Invocations.Single(i => i.Identifier == "ipop.phone.qrSvg");
        Assert.Equal("https://ipop.santos.co.id/app", qr.Arguments[0]);
        Assert.Equal(2, cut.FindAll("details.ph-how").Count);
        Assert.All(cut.FindAll("details.ph-how"), d => Assert.False(d.HasAttribute("open")));
    }

    [Fact]
    public void Switching_the_segment_shows_the_other_platform()
    {
        using var ctx = Ctx("android", apk: true);
        var cut = ctx.Render<AppInstall>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("a.ph-dl")));
        cut.Find("[role=tab][data-value=ios]").Click();
        Assert.Contains("Add to Home Screen", cut.Markup);
        Assert.Empty(cut.FindAll("a.ph-dl"));
    }

    [Fact]
    public void Page_is_anonymous_and_uses_the_empty_layout()
    {
        var razor = File.ReadAllText(Path.Combine(Web, "Pages", "AppInstall.razor"));
        Assert.Contains("@page \"/app\"", razor);
        Assert.Contains("@layout EmptyLayout", razor);
        var app = File.ReadAllText(Path.Combine(Web, "App.razor"));
        Assert.Matches(@"PublicPages\s*=\s*\[[^\]]*""app""", app);
    }

    [Fact]
    public void Guard_lets_a_signed_out_visitor_open_app_and_a_signed_in_user_stay_there()
    {
        Assert.True(IPop.Web.App.IsPublic("app"));
        Assert.False(IPop.Web.App.IsPublic("chat"));
    }
}
