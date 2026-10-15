using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bunit;
using IPop.Web.Components.Mail;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using WebMail = IPop.Web.Services;

namespace IPop.Tests;

/// Mail QA 3 (2026-10-14): EPop group chips squeezed to "SJ…" on the To line and the thread item header, and
/// the big empty gaps EPop/Outlook bodies carry (empty paragraphs, &lt;br&gt; runs, fixed-height spacers).
public class MailQa3Tests
{
    private const string Me = "ME01";

    private static WebMail.MailRecipient R(string nik, string name, string type) => new(Guid.NewGuid(), name, nik, $"{nik}@x.co", type);

    private static MailDetail Detail(string sender, params WebMail.MailRecipient[] r) => new(
        Guid.NewGuid(), Guid.NewGuid(), sender, sender, "Re: CA Online", "<p>x</p>", true, null,
        DateTime.UtcNow, null, null, 0, false, true, "inbox", null, null, r, [], []);

    private static RecipientGroupCount G(string name, int count = 1) => new("to", "epop", Guid.Empty, name, count);

    private static MailDetail Epop() => Detail("PP01", R(Me, "RM BAGASTYO ANGGORO", "to")) with
    {
        RecipientTotal = 5,
        RecipientGroups = [G("SJA JAKARTA"), G("SJA KARAWANG"), G("SJA SEMARANG"), G("SJA SEPANJANG"), G("SJA SUKODONO")],
    };

    private static BunitContext Ctx()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new NoApi()) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new WebMail.MailService(auth, http));
        ctx.Services.AddSingleton<MailReceiptsStore>();
        ctx.Services.AddSingleton<MailReadingPrefs>();
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }

    private sealed class NoApi : HttpMessageHandler
    {
        public Func<string, object?> Route { get; init; } = _ => null;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = Route(request.RequestUri!.PathAndQuery);
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
            });
        }
    }

    // ───────────── readable group names ─────────────

    [Theory]
    [InlineData("SJA JAKARTA", "SJA Jakarta")]
    [InlineData("ICT · SJA KARAWANG", "ICT · SJA Karawang")]
    [InlineData("HCBP · SJA SEMARANG", "HCBP · SJA Semarang")]
    [InlineData("DAFT · SJA SUKODONO", "DAFT · SJA Sukodono")]
    [InlineData("PLANT-UTILITY · SJA SEMARANG", "Plant-Utility · SJA Semarang")]
    [InlineData("Research & Development · SJA SEPANJANG", "Research & Development · SJA Sepanjang")]
    [InlineData("RIKKI RINALDO", "Rikki Rinaldo")]
    [InlineData("RANY TRESIA SITUMORANG", "Rany Tresia Situmorang")]
    [InlineData("BENY SAPUTRA, S.KOM", "Beny Saputra, S.KOM")]
    [InlineData("Operational Support · Network & Infrastructure", "Operational Support · Network & Infrastructure")]
    [InlineData("Sulastri Basrewan", "Sulastri Basrewan")]
    public void Group_names_read_in_title_case_keeping_short_acronyms(string raw, string shown)
        => Assert.Equal(shown, MailRecipientLine.GroupTitle(raw));

    // ───────────── collapsed To line: full chips, then +N ─────────────

    [Fact]
    public void Epop_to_line_shows_you_then_whole_readable_chips_then_more()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<MailRecipientsHeader>(p => p.Add(c => c.Detail, Epop()).Add(c => c.MyNik, Me));
        var to = cut.Find(".mrh-line[data-field=to]");
        var shown = to.QuerySelectorAll(":scope > .mrh-it").Select(e => e.TextContent.Trim().TrimEnd(',')).ToList();
        // Before the browser measures: you + the first two groups, the rest behind +N.
        Assert.Equal(["you", "SJA Jakarta", "SJA Karawang"], shown);
        Assert.Equal("+3", to.QuerySelector(":scope > .mrh-more")!.TextContent.Trim());
        Assert.DoesNotContain("SJA JAKARTA", to.QuerySelector(":scope > .mrh-chip")!.TextContent);
        Assert.Equal("SJA JAKARTA: 1 people", to.QuerySelector(":scope > .mrh-chip")!.GetAttribute("title"));
    }

    [Fact]
    public void Measured_fit_from_the_browser_sets_how_many_whole_items_show()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<MailRecipientsHeader>(p => p.Add(c => c.Detail, Epop()).Add(c => c.MyNik, Me));
        // The measuring row carries every item so the browser can size them; it is hidden from people and AT.
        var measure = cut.Find(".mrh-line[data-field=to] .mrh-msr");
        Assert.Equal("true", measure.GetAttribute("aria-hidden"));
        Assert.Equal(6, measure.QuerySelectorAll("[data-fit]").Count);
        Assert.Equal("3", cut.Find(".mrh-line[data-field=to]").GetAttribute("data-shown"));

        cut.InvokeAsync(() => cut.Instance.OnFit("to", 5));
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrh-line[data-field=to] > .mrh-it").Count));
        Assert.Equal("+1", cut.Find(".mrh-line[data-field=to] > .mrh-more").TextContent.Trim());

        cut.InvokeAsync(() => cut.Instance.OnFit("to", 6));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mrh-line[data-field=to] > .mrh-more")));

        // Never fewer than one item, never more than there are.
        cut.InvokeAsync(() => cut.Instance.OnFit("to", 0));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mrh-line[data-field=to] > .mrh-it")));
        Assert.Equal("+5", cut.Find(".mrh-line[data-field=to] > .mrh-more").TextContent.Trim());
    }

    [Fact]
    public void Fit_is_wired_to_the_browser_and_chips_are_capped_not_squeezed()
    {
        var root = Root();
        var header = File.ReadAllText(Path.Combine(root, "src/IPop.Web/Components/Mail/MailRecipientsHeader.razor"));
        Assert.Contains("ipopMailFit.bind", header);
        Assert.Contains("[JSInvokable]", header);
        var index = File.ReadAllText(Path.Combine(root, "src/IPop.Web/wwwroot/index.html"));
        Assert.Contains("js/mail-fit.js", index);
        var css = File.ReadAllText(Path.Combine(root, "src/IPop.Web/wwwroot/css/v6/mail-receipts.css"));
        var chip = Rule(css, ".mrh-line > .mrh-it");
        Assert.Matches(@"flex:\s*none", chip);
        Assert.Matches(@"max-width:\s*220px", chip);
    }

    // ───────────── thread item header ─────────────

    [Fact]
    public void Thread_to_line_reads_you_and_title_case_groups_with_a_count_that_never_truncates()
    {
        var line = MailRecipientLine.ThreadTo(Epop(), Me);
        Assert.Equal("you, SJA Jakarta", line.Names);
        Assert.Equal(4, line.More);
        Assert.Equal("you", line.Narrow);        // a phone row: one whole item, then the count
        Assert.Equal(5, line.NarrowMore);

        var plain = Detail("S1", R("T1", "Tono", "to"), R("C1", "Cici", "cc"));
        var p = MailRecipientLine.ThreadTo(plain, Me);
        Assert.Equal("Tono, Cici", p.Names);
        Assert.Equal(0, p.More);

        var hidden = Detail("S1") with { HideRecipients = true, RecipientTotal = 30 };
        Assert.Equal("undisclosed recipients", MailRecipientLine.ThreadTo(hidden, Me).Names);
    }

    [Fact]
    public void Open_thread_item_puts_the_to_line_under_the_name_with_the_count_outside_the_ellipsis()
    {
        // Outlook layout (2026-10-15): the recipients line ("To you, … · Cc … +N") is its own row under the name;
        // the names ellipsize, the +N button and Details never do.
        var root = Root();
        var stack = File.ReadAllText(Path.Combine(root, "src/IPop.Web/Components/Mail/MailThreadStack.razor"));
        Assert.Contains("MailRecipientLine.Compact(", stack);
        Assert.Contains("thr-rl", stack);
        var css = File.ReadAllText(Path.Combine(root, "src/IPop.Web/wwwroot/css/v6/mail-layout.css"));
        Assert.Matches(@"text-overflow:\s*ellipsis", Rule(css, ".mlx .thr-rcp"));
        Assert.Matches(@"flex:\s*none", Rule(css, ".mlx .thr-rl .mrh-trig"));
        Assert.Matches(@"flex:\s*none", Rule(css, ".mlx .thr-dtg"));
        Assert.Matches(@"text-overflow:\s*ellipsis", Rule(css, ".mlx .thr-name"));
    }

    private static string Rule(string css, string selector)
        => Regex.Match(css, Regex.Escape(selector) + @"\s*\{([^}]*)\}").Groups[1].Value;

    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IPop.slnx"))) root = root.Parent;
        return root!.FullName;
    }
}
