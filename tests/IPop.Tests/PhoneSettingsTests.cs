using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Web.Components;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

[Collection("AvatarPhotos")]
public class PhoneSettingsTests
{
    private sealed class Api : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add((r.Method.Method, r.RequestUri!.PathAndQuery, r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct)));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DirectoryEmployeeDto("PRIVATE-NIK", "Jane Smith", "PRIVATE-POSITION", "ICT", "ict", "Sepanjang", "sja", "Mary", "jane@example.invalid", "1204", null, "on", [])) };
        }
    }
    /// <paramref name="phone"/> null = cold start: the browser has not answered ipop.mobile.watch yet.
    private static BunitContext Context(bool? phone, Api api)
    {
        var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api) { BaseAddress = new("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime); ctx.Services.AddSingleton(auth);
        var state = new PhoneState(ctx.JSInterop.JSRuntime); if (phone is { } p) state.OnPhoneChanged(p);
        ctx.Services.AddSingleton(state); ctx.Services.AddSingleton(new OverlayStack());
        ctx.Services.AddSingleton(new ToastService()); ctx.Services.AddSingleton(new NotificationSettingsState()); ctx.Services.AddSingleton(new ConfirmService());
        ctx.Services.AddSingleton(new TabPrefsState(auth));
        return ctx;
    }
    [Fact]
    public void Phone_profile_has_locked_HRIS_and_real_contact_edits_without_identity_fields()
    {
        var api = new Api(); using var ctx = Context(true, api);
        var cut = ctx.Render<AccountSheet>(p => p.Add(x => x.IsOpen, true));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".m-profile")));
        Assert.DoesNotContain("PRIVATE-NIK", cut.Find(".m-profile").TextContent);
        Assert.DoesNotContain("PRIVATE-POSITION", cut.Markup);
        Assert.Contains("from HRIS", cut.Markup); Assert.Contains("Mary", cut.Markup);
        cut.Find("button[aria-label='Edit email']").Click();
        cut.Find("input[aria-label='Email']").Input("new@example.invalid");
        cut.Find("form.m-profile-email").Submit();
        cut.WaitForAssertion(() => Assert.Contains(api.Requests, r => r.Method == "PUT" && r.Path == "/api/me/email" && r.Body.Contains("new@example.invalid")));
        cut.Find("button[aria-label='Edit extension']").Click();
        cut.Find("input[aria-label='Phone extension']").Input("7788");
        cut.Find("form.m-profile-extension").Submit();
        cut.WaitForAssertion(() => Assert.Contains(api.Requests, r => r.Method == "PUT" && r.Path == "/api/me/extension" && r.Body.Contains("7788")));
    }
    [Fact]
    public async Task Phone_profile_Back_closes_kit_sheet_and_desktop_keeps_account_DOM()
    {
        using var ctx = Context(true, new Api()); var closed = false;
        var cut = ctx.Render<AccountSheet>(p => p.Add(x => x.IsOpen, true).Add(x => x.IsOpenChanged, v => closed = !v));
        await cut.InvokeAsync(() => ctx.Services.GetRequiredService<OverlayStack>().CloseTopAsync()); Assert.True(closed);
        using var desktop = Context(false, new Api());
        var pc = desktop.Render<AccountSheet>(p => p.Add(x => x.IsOpen, true));
        Assert.Single(pc.FindAll(".acct-sheet")); Assert.Empty(pc.FindAll(".m-profile, .ph-sheet"));
    }
    [Fact]
    public void Settings_phone_hub_dispatches_existing_preferences_and_has_no_language_or_fake_version()
    {
        using var ctx = Context(true, new Api()); var cut = ctx.Render<SettingsPhone>();
        Assert.Equal(new[] { "Light", "Dark", "Automatic" }, cut.FindAll(".m-settings-appearance button").Select(x => x.TextContent.Trim()));
        Assert.DoesNotContain("Language", cut.Markup); Assert.DoesNotContain("2026.10.16", cut.Markup);
        cut.FindAll(".ph-row").Single(x => x.TextContent.Contains("Edit tabs")).Click(); Assert.Single(cut.FindAll(".ph-sheet"));
        cut.Find("button[aria-label='Spell check']").Click();
        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "ipop.spellcheck" && i.Arguments.Count == 1);
        cut.FindAll(".ph-row").Single(x => x.TextContent.Contains("Notifications & sounds")).Click();
        Assert.True(ctx.Services.GetRequiredService<NotificationSettingsState>().IsOpen);
    }
    [Fact]
    public void Phone_profile_route_stays_on_profile_and_renders_real_account_editor()
    {
        using var ctx = Context(true, new Api());
        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(); nav.NavigateTo("/profile");
        var cut = ctx.Render<Profile>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".m-profile")));
        Assert.EndsWith("/profile", nav.Uri);
    }
    [Theory]
    [InlineData("profile")]
    [InlineData("settings")]
    public void Cold_phone_deeplink_waits_for_phone_detection_before_desktop_redirect(string route)
    {
        using var ctx = Context(null, new Api());
        ctx.JSInterop.Setup<bool>("ipop.mobile.watch", _ => true).SetResult(true);
        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(); nav.NavigateTo("/" + route);
        if (route == "profile") { var cut = ctx.Render<Profile>(); cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".m-profile"))); }
        else { var cut = ctx.Render<SettingsPhone>(); cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".m-settings"))); }
        Assert.EndsWith("/" + route, nav.Uri);
    }
    [Fact]
    public void Desktop_settings_redirects_to_profile_without_phone_markup()
    {
        using var ctx = Context(false, new Api()); var cut = ctx.Render<SettingsPhone>();
        Assert.Empty(cut.FindAll(".m-settings"));
        Assert.EndsWith("/profile", ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }
}
