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
    private sealed class Api(bool empty = false, bool fail = false) : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method.Method + " " + request.RequestUri!.AbsolutePath);
            if (fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AdminNotifier.ReleasesDto(
                    new("1.2.2", false, null), new("1.3.0", true, null),
                    empty ? [] : [new("1.3.0", 2, 1), new("1.2.2", 5, 3)])),
            });
        }
    }

    [Fact]
    public void Displays_released_pilot_and_pc_versions_without_write_controls()
    {
        var api = new Api();
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new AuthState(new HttpClient(api) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime));
        var cut = ctx.Render<AdminNotifier>();
        cut.WaitForAssertion(() => Assert.Contains("Everyone: 1.2.2", cut.Markup));
        Assert.Contains("Pilot (ICT/Admin): 1.3.0", cut.Markup);
        Assert.Contains("7 PCs", cut.Markup);
        Assert.Contains("mandatory", cut.Markup);
        Assert.Empty(cut.FindAll("button,input,select,textarea"));
        Assert.Equal("GET /api/notifier/releases", Assert.Single(api.Methods));
    }

    [Fact]
    public void Request_failure_is_not_presented_as_zero_devices()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
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
        ctx.Services.AddSingleton(new AuthState(new HttpClient(new Api(empty: true)) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime));
        var cut = ctx.Render<AdminNotifier>();
        cut.WaitForAssertion(() => Assert.Contains("No PC has reported in the last 14 days.", cut.Markup));
    }
}
