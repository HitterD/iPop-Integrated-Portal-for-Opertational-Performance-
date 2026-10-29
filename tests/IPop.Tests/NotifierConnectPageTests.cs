using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// /notifier/connect (1.4.2): the card a person clicks to sign the iPop Notifier on this PC in.
public class NotifierConnectPageTests
{
    private static readonly string Request = new('r', 43);

    private sealed class Api(object card, HttpStatusCode approve = HttpStatusCode.NoContent) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public bool Connected { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.Method.Method + " " + path);
            HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
            return Task.FromResult(path switch
            {
                "/api/directory/me" => Json(new { nik = "0001", name = "BAGASTYO PUTRA", roles = Array.Empty<string>() }),
                var p when p.StartsWith("/api/notifier/link/request/") => Json(card),
                "/api/notifier/link/approve" => new HttpResponseMessage(approve),
                "/api/notifier/link/deny" => new HttpResponseMessage(HttpStatusCode.NoContent),
                "/api/notifier/link/status" => Json(new { connected = Connected, name = "Bagastyo Putra", known = true }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }
    }

    private static object Card(bool same = true, string state = "pending", bool switching = false, int age = 5) =>
        new { pcName = "SJA-PC", createdAt = DateTime.UtcNow, ageSeconds = age, sameAddress = same, expired = state == "expired", state, switching };

    private static (BunitContext Ctx, Api Api) Ctx(object card, bool opened = false, HttpStatusCode approve = HttpStatusCode.NoContent)
    {
        var api = new Api(card, approve);
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<bool>("ipop.notifierLink.openedTab").SetResult(opened);
        ctx.JSInterop.Setup<bool>("ipop.notifierLink.close").SetResult(false);
        var auth = new AuthState(new HttpClient(api) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton(sp => new NotifierLink(auth, sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), sp.GetRequiredService<NavigationManager>()));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/notifier/connect?request=" + Request);
        return (ctx, api);
    }

    [Fact]
    public void Shows_the_pc_the_account_and_when_and_connects_only_on_a_click()
    {
        var (ctx, api) = Ctx(Card());
        using var _ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("Connect iPop Notifier on this PC?", cut.Markup));
        Assert.Contains("SJA-PC", cut.Markup);
        Assert.Contains("as Bagastyo Putra", cut.Markup);
        Assert.Contains("just now", cut.Markup);
        Assert.DoesNotContain(api.Calls, c => c.Contains("approve"));                      // nothing happens by itself
        cut.Find("button.nc-btn.primary").Click();
        cut.WaitForAssertion(() => Assert.Contains("POST /api/notifier/link/approve", api.Calls));
        cut.WaitForAssertion(() => Assert.Contains("Connecting…", cut.Markup));
    }

    [Fact]
    public void An_app_opened_tab_says_connected_and_closes()
    {
        var (ctx, api) = Ctx(Card(), opened: true);
        using var _ = ctx;
        api.Connected = true;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("Connect iPop Notifier on this PC?", cut.Markup));
        cut.Find("button.nc-btn.primary").Click();
        cut.WaitForAssertion(() => Assert.Contains("Closing this tab in 2 s", cut.Markup), TimeSpan.FromSeconds(5));
        Assert.Contains("Signed in as", cut.Markup);
        Assert.Contains("Bagastyo Putra", cut.Markup);
    }

    [Fact]
    public void Another_account_on_the_app_asks_connect_as_me()
    {
        var (ctx, _) = Ctx(Card(switching: true));
        using var __ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("Connect as Bagastyo Putra?", cut.Markup));
    }

    [Fact]
    public void Another_pc_gets_no_connect_button()
    {
        var (ctx, _) = Ctx(Card(same: false));
        using var __ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("Open this page on the PC where iPop Notifier is waiting", cut.Markup));
        Assert.Empty(cut.FindAll("button.nc-btn.primary"));
    }

    [Fact]
    public void An_expired_request_says_so()
    {
        var (ctx, _) = Ctx(Card(state: "expired", age: 150));
        using var __ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("This request expired. Click Sign in with browser in iPop Notifier again.", cut.Markup));
        Assert.Empty(cut.FindAll("button.nc-btn.primary"));
    }

    [Fact]
    public void Cancel_denies_the_request()
    {
        var (ctx, api) = Ctx(Card(), opened: true);
        using var _ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("Cancel", cut.Markup));
        cut.Find("button.nc-btn.secondary").Click();
        cut.WaitForAssertion(() => Assert.Contains("POST /api/notifier/link/deny", api.Calls));
        cut.WaitForAssertion(() => Assert.Contains("iPop Notifier was not connected", cut.Markup));
        Assert.DoesNotContain(api.Calls, c => c.Contains("approve"));
    }

    [Fact]
    public void Approve_refused_from_another_address_keeps_the_card_with_a_note()
    {
        var (ctx, _) = Ctx(Card(), approve: HttpStatusCode.Forbidden);
        using var __ = ctx;
        var cut = ctx.Render<NotifierConnect>();
        cut.WaitForAssertion(() => Assert.Contains("Connect iPop Notifier on this PC?", cut.Markup));
        cut.Find("button.nc-btn.primary").Click();
        cut.WaitForAssertion(() => Assert.Contains("Open this page on the PC where iPop Notifier is waiting", cut.Markup));
    }
}
