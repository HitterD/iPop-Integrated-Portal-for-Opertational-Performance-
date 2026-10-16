using System.Net;
using Bunit;
using IPop.Web.Components.Phone;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Phone notification card (mockup v1 frame o): one friendly prompt instead of the permanent banner.
public class NotifCardTests
{
    private static readonly string Web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "IPop.Web"));

    [Theory]
    //          phone  dismissed server secure  permission     optedOut  expected
    [InlineData(true,  false,    true,  true,   "default",     false,    true)]
    [InlineData(false, false,    true,  true,   "default",     false,    false)]   // desktop keeps the banner
    [InlineData(true,  true,     true,  true,   "default",     false,    false)]   // answered once
    [InlineData(true,  false,    false, true,   "default",     false,    false)]   // push off on the server
    [InlineData(true,  false,    true,  true,   "granted",     false,    false)]
    [InlineData(true,  false,    true,  true,   "denied",      false,    false)]
    [InlineData(true,  false,    true,  true,   "default",     true,     false)]   // turned off in this browser
    [InlineData(true,  false,    true,  false,  "unsupported", false,    true)]    // http LAN: explains why
    public void Card_shows_once_on_phones_while_permission_is_undecided(bool phone, bool dismissed, bool server, bool secure, string permission, bool optedOut, bool expected)
        => Assert.Equal(expected, NotificationCard.ShouldShow(phone, dismissed, server, secure, permission, optedOut));

    private sealed class Api : HttpMessageHandler
    {
        public string? Saved;
        public List<string> Puts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/directory/me")
                return new(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(new DirectoryEmployeeDto("N1", "Sari", null, null, null, null, null, null, "s@x", null, null, "on")) };
            if (path == "/api/me/ui-prefs/notif-card")
            {
                if (req.Method == HttpMethod.Put) { Saved = await req.Content!.ReadAsStringAsync(ct); Puts.Add(Saved); return new(HttpStatusCode.NoContent); }
                return Saved is null ? new(HttpStatusCode.NoContent) : new(HttpStatusCode.OK) { Content = new StringContent(Saved) };
            }
            return new(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Not_now_saves_dismissed_to_the_account()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new Api();
        var auth = new AuthState(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") }, ctx.JSInterop.JSRuntime);
        await auth.LoadMeAsync();
        var state = new NotifCardState(auth);
        await state.LoadAsync();
        Assert.False(state.Answered);

        await state.AnswerAsync("dismissed");
        Assert.True(state.Answered);
        Assert.Equal(["\"dismissed\""], api.Puts);

        var again = new NotifCardState(auth);     // another device / next start
        await again.LoadAsync();
        Assert.True(again.Answered);
    }

    [Fact]
    public void Card_has_the_mockup_copy_and_fires_its_buttons()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        int on = 0, later = 0;
        var cut = ctx.Render<NotificationCard>(p => p.Add(x => x.Visible, true).Add(x => x.Secure, true)
            .Add(x => x.OnTurnOn, () => on++).Add(x => x.OnNotNow, () => later++));
        Assert.Equal("Get notified about chats, mail and calls", cut.Find(".ph-npc h2").TextContent);
        Assert.Contains("Only new messages, mail addressed to you and incoming calls.", cut.Markup);
        cut.Find(".ph-npc .ph-btn").Click();
        cut.Find(".ph-npc .ph-btn.pl").Click();
        Assert.Equal((1, 1), (on, later));
    }

    [Fact]
    public void Insecure_address_explains_instead_of_offering_turn_on()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        var cut = ctx.Render<NotificationCard>(p => p.Add(x => x.Visible, true).Add(x => x.Secure, false));
        Assert.Contains("https://ipop.santos.co.id", cut.Markup);
        Assert.DoesNotContain("Turn on", cut.Markup);
        Assert.Equal("OK", cut.Find(".ph-npc .ph-btn").TextContent.Trim());
    }

    [Fact]
    public void Layout_keeps_the_desktop_banner_desktop_only_and_shows_the_card_on_phones()
    {
        var razor = File.ReadAllText(Path.Combine(Web, "Layout", "MainLayout.razor"));
        var desk = razor.IndexOf("<DesktopOnly>", StringComparison.Ordinal);
        var banner = razor.IndexOf("class=\"push-banner\" role=\"status\"", StringComparison.Ordinal);
        var end = razor.IndexOf("</DesktopOnly>", desk, StringComparison.Ordinal);
        Assert.True(desk >= 0 && desk < banner && banner < end, "push banner must sit inside <DesktopOnly>");
        Assert.Contains("<NotificationCard", razor);
    }
}
