using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Web.Components.Admin;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class AdminNotifierTests
{
    private sealed class Api(bool empty = false, bool fail = false, bool noPilot = false, AdminNotifier.SyncDto? sync = null, string? msi = null) : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public bool Promoted { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method.Method + " " + request.RequestUri!.AbsolutePath);
            if (fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (request.Method == HttpMethod.Post) { Promoted = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { version = "1.3.0" }) }); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AdminNotifier.ReleasesDto(
                    Promoted ? new("1.3.0", true, null) : new("1.2.2", false, null), Promoted || noPilot ? null : new("1.3.0", true, null),
                    empty ? [] : [new("1.3.0", 2, 1), new("1.2.2", 5, 3)], sync, msi)),
            });
        }
    }

    [Fact]
    public void Release_to_everyone_asks_then_promotes_the_pilot()
    {
        var api = new Api();
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<ToastService>();
        ctx.Services.AddSingleton<ConfirmService>();
        ctx.Services.AddSingleton(new AuthState(new HttpClient(api) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime));
        var cut = ctx.Render<AdminNotifier>();
        cut.WaitForAssertion(() => Assert.Contains("Release 1.3.0 to everyone", cut.Markup));
        cut.Find("button.adm-btn.primary").Click();
        Assert.DoesNotContain("POST /api/notifier/releases/promote", api.Methods);   // waits for the confirm
        cut.InvokeAsync(() => ctx.Services.GetRequiredService<ConfirmService>().Resolve(true));
        cut.WaitForAssertion(() => Assert.Contains("POST /api/notifier/releases/promote", api.Methods));
        cut.WaitForAssertion(() => Assert.Contains("Everyone: 1.3.0", cut.Markup));
        Assert.Empty(cut.FindAll("button.adm-btn.primary"));
    }

    [Fact]
    public void Shows_sync_failure_and_the_gpo_msi_download()
    {
        var api = new Api(noPilot: true, sync: new(DateTime.UtcNow, DateTime.UtcNow.AddHours(-2), "GitHub answered 401 for the release list.", 4, "1.3.0"), msi: "iPopNotifier-win-GPO.msi");
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<ToastService>();
        ctx.Services.AddSingleton<ConfirmService>();
        ctx.Services.AddSingleton(new AuthState(new HttpClient(api) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime));
        var cut = ctx.Render<AdminNotifier>();
        cut.WaitForAssertion(() => Assert.Contains("GitHub sync failing (4 tries)", cut.Markup));
        Assert.Contains("401", cut.Markup);
        Assert.Equal("/download/notifier/iPopNotifier-win-GPO.msi", cut.Find("a[download]").GetAttribute("href"));
        Assert.Empty(cut.FindAll("button.adm-btn.primary"));
    }

    [Fact]
    public void Displays_released_pilot_and_pc_versions()
    {
        var api = new Api();
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<ToastService>();
        ctx.Services.AddSingleton<ConfirmService>();
        ctx.Services.AddSingleton(new AuthState(new HttpClient(api) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime));
        var cut = ctx.Render<AdminNotifier>();
        cut.WaitForAssertion(() => Assert.Contains("Everyone: 1.2.2", cut.Markup));
        Assert.Contains("Pilot (ICT/Admin): 1.3.0", cut.Markup);
        Assert.Contains("7 PCs", cut.Markup);
        Assert.Contains("mandatory", cut.Markup);
        Assert.Empty(cut.FindAll("input,select,textarea"));
        Assert.Equal("GET /api/notifier/releases", Assert.Single(api.Methods));
    }

    [Fact]
    public void Request_failure_is_not_presented_as_zero_devices()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<ToastService>();
        ctx.Services.AddSingleton<ConfirmService>();
        ctx.Services.AddSingleton(new AuthState(new HttpClient(new Api(fail: true)) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime));
        var cut = ctx.Render<AdminNotifier>();
        cut.WaitForAssertion(() => Assert.Contains("Couldn't load Notifier versions", cut.Markup));
        Assert.DoesNotContain("No PC has reported", cut.Markup);
    }

    [Fact]
    public void No_devices_has_an_explicit_empty_state()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<ToastService>();
        ctx.Services.AddSingleton<ConfirmService>();
        ctx.Services.AddSingleton(new AuthState(new HttpClient(new Api(empty: true)) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime));
        var cut = ctx.Render<AdminNotifier>();
        cut.WaitForAssertion(() => Assert.Contains("No PC has reported in the last 14 days.", cut.Markup));
    }
}
