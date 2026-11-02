using System.Net;
using System.Text;
using Bunit;
using IPop.Web.Components;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// "My approvers" on the own profile: labels and names, never NIKs.
public class ProfileApproversRenderTests
{
    private sealed class Api(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static IRenderedComponent<MyApprovers> Render(BunitContext ctx, string json)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new AuthState(new HttpClient(new Api(json)) { BaseAddress = new("http://localhost/") }, ctx.JSInterop.JSRuntime));
        return ctx.Render<MyApprovers>();
    }

    [Fact]
    public void Profile_ShowsCompactedChainWithoutNik()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, """[{"label":"Manager","name":"Muhammad Rifa'i","active":true},{"label":"General Manager","name":"Tan Novilia Astan","active":true}]""");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".my-apv li").Count));
        Assert.Contains("Manager", cut.Markup);
        Assert.Contains("Tan Novilia Astan", cut.Markup);
        Assert.DoesNotMatch(@"\d{8}", cut.Markup);
    }

    [Fact]
    public void Profile_EmptyChainMessage()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, "[]");
        cut.WaitForAssertion(() => Assert.Contains("No approvers set yet. Contact HC if this looks wrong.", cut.Markup));
    }
}
