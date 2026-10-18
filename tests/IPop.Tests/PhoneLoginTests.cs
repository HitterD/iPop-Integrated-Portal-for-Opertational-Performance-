using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Web.Components.Phone;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace IPop.Tests;
public class PhoneLoginTests
{
    private sealed class Api : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new DirectoryEmployeeDto("PRIVATE-NIK", "Jane", "PRIVATE-POSITION", "ICT", "ict", "SJA", "sja", null, "jane@example.invalid", null, null, "on", [])) });
    }
    private static BunitContext Ctx(bool phone)
    {
        var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<bool>("ipop.mobile.watch", _ => true).SetResult(phone);
        var state = new PhoneState(ctx.JSInterop.JSRuntime); state.OnPhoneChanged(phone); ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton(new AuthState(new HttpClient(new Api()) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(new OverlayStack()); return ctx;
    }
    [Fact]
    public void Phone_login_uses_brand_variants_employee_ID_eye_and_browser_install_hint()
    {
        using var ctx = Ctx(true); var cut = ctx.Render<Login>();
        Assert.Single(cut.FindAll(".m-login")); Assert.Empty(cut.FindAll(".auth-slides"));
        Assert.Equal(2, cut.FindAll("img[alt='Kapal Api Group']").Count); Assert.Equal(2, cut.FindAll("img[alt='PT Santos Jaya Abadi']").Count);
        Assert.Contains("Employee ID", cut.Markup); cut.Find("button[aria-label='Show password']").Click(); Assert.Equal("text", cut.Find("#pwd").GetAttribute("type"));
        Assert.Single(cut.FindAll("a[href='/app']"));
    }
    [Fact]
    public void Desktop_login_does_not_render_phone_install_hint()
    {
        using var ctx = Ctx(false); var cut = ctx.Render<Login>(); Assert.Empty(cut.FindAll(".m-login, .m-login-install")); Assert.Single(cut.FindAll(".auth-card"));
    }
    [Fact]
    public void Onboarding_has_three_steps_edit_tabs_theme_notification_and_persists_skip_per_account()
    {
        using var ctx = Ctx(true); var tabs = false; var notifications = false;
        var cut = ctx.Render<Onboarding>(p => p.Add(x => x.OnEditTabs, () => tabs = true).Add(x => x.OnNotifications, () => notifications = true));
        cut.WaitForAssertion(() => Assert.Contains("Everything in one app", cut.Markup));
        cut.Find("button[aria-label='Next']").Click(); Assert.Contains("Make it yours", cut.Markup);
        cut.Find("button[aria-label='Edit tabs']").Click(); Assert.True(tabs);
        cut.Find("button[aria-label='Next']").Click(); cut.Find("button[aria-label='Turn on notifications']").Click(); Assert.True(notifications);
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "ipopUserStore.set" && i.Arguments.Contains("onboarded")));
        Assert.DoesNotContain("PRIVATE-NIK", cut.Markup);
    }
    [Fact]
    public void Onboarding_never_reappears_after_saved_preference()
    {
        using var ctx = Ctx(true); ctx.JSInterop.Setup<string?>("ipopUserStore.get", _ => true).SetResult("1");
        var cut = ctx.Render<Onboarding>(); Assert.Empty(cut.FindAll(".m-onboarding"));
    }
}
