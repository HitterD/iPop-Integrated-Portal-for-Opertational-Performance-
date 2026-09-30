using System.Net;
using System.Net.Http.Json;
using Bunit;
using IPop.Web.Components;
using IPop.Web.Components.Mail;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// The shared recipient combobox (Mail composer, News, Projects, Admin, Calls).
public class AppMemberPickerTests
{
    /// Answers /api/directory/member-search with one row named after the query.
    private sealed class SearchHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["q"] ?? "";
            var rows = new[] { new MemberSearchRow("NIK-" + q, "Person " + q, "Staff", "IT") };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(rows) });
        }
    }

    private static BunitContext NewContext(SearchHandler handler)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(new AuthState(http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(http);
        return ctx;
    }

    [Fact]
    public void Enter_picks_the_current_result_then_clears_the_box()
    {
        using var ctx = NewContext(new SearchHandler());
        var picked = new List<MemberSearchRow?>();
        var cut = ctx.Render<AppMemberPicker>(p => p.Add(c => c.ValueChanged, (MemberSearchRow? r) => picked.Add(r)));

        cut.Find("input").Input("budi");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".member-picker-opt")), TimeSpan.FromSeconds(3));

        cut.Find("input").KeyDown("Enter");

        cut.WaitForAssertion(() =>
        {
            var row = Assert.Single(picked);
            Assert.Equal("NIK-budi", row!.Nik);
            Assert.Equal("", cut.Find("input").GetAttribute("value"));
            Assert.Empty(cut.FindAll(".member-picker-opt"));
        }, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Starting_a_new_query_drops_stale_results_and_Enter_waits_for_fresh_ones()
    {
        var handler = new SearchHandler();
        using var ctx = NewContext(handler);
        var picked = new List<MemberSearchRow?>();
        var cut = ctx.Render<AppMemberPicker>(p => p.Add(c => c.ValueChanged, (MemberSearchRow? r) => picked.Add(r)));

        cut.Find("input").Input("bu");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".member-picker-opt")), TimeSpan.FromSeconds(3));

        // Typing more: the "bu" suggestions must disappear at once, and Enter during the
        // debounce/load must not pick the old "Person bu".
        cut.Find("input").Input("bud");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".member-picker-opt")), TimeSpan.FromMilliseconds(150));
        cut.Find("input").KeyDown("Enter");
        Assert.Empty(picked);

        cut.WaitForAssertion(() => Assert.Contains("Person bud", cut.Find(".member-picker-opt").TextContent), TimeSpan.FromSeconds(3));
        cut.Find("input").KeyDown("Enter");
        cut.WaitForAssertion(() => Assert.Equal("NIK-bud", Assert.Single(picked)!.Nik), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Escape_clears_the_typed_text_and_the_suggestions()
    {
        using var ctx = NewContext(new SearchHandler());
        var cut = ctx.Render<AppMemberPicker>(p => p.Add(c => c.ValueChanged, (MemberSearchRow? _) => { }));

        cut.Find("input").Input("sari");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".member-picker-opt")), TimeSpan.FromSeconds(3));

        cut.Find("input").KeyDown("Escape");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("", cut.Find("input").GetAttribute("value"));
            Assert.Empty(cut.FindAll(".member-picker-opt"));
        }, TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("<p><br></p>", true)]
    [InlineData("<p>&nbsp;</p><div> </div>", true)]
    [InlineData("<p>Hi Budi</p>", false)]
    [InlineData("<p><img src=\"x.png\"></p>", false)]
    public void Composer_treats_only_blank_markup_as_an_empty_body(string? html, bool empty)
        => Assert.Equal(empty, MailComposer.IsBodyEmpty(html));
}
