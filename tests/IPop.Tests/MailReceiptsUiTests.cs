using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using IPop.Web.Components.Mail;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using WebMail = IPop.Web.Services;

namespace IPop.Tests;

/// Mail read receipts UI (spec 2026-10-14 §1, §4, §6, mass panel): the summary row, the Read by / Not yet sheet,
/// the collapsed recipients header + panel, the EPop list marker and the sender's mass panel on /receipts.
public class MailReceiptsUiTests
{
    private const string Me = "ME01";
    private static readonly DateTime Since = new(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc);

    private static MailReceiptPerson P(string key, string name, string field = "to", DateTime? seen = null, bool me = false,
        string? dept = "ICT", string? site = "SJA Karawang", bool viaGroup = false, bool delivered = true)
        => new(key, name, dept, site, null, field, viaGroup, me, seen, delivered);

    private static MailReceipts Rc(IReadOnlyList<MailReceiptPerson> people, IReadOnlyList<MailReceiptGroup>? groups = null,
        bool tracked = true, bool namesVisible = true, bool isSender = false, int? total = null, int? read = null, int external = 0)
        => new(Guid.NewGuid(), tracked, Since, isSender, namesVisible,
            total ?? people.Count, read ?? people.Count(p => p.SeenAt is not null), total ?? people.Count(p => p.Delivered),
            people, groups ?? [], new MailReceiptExternal(external, "not_available"));

    // ───────────── summary row ─────────────

    [Fact]
    public void Group_mail_summary_counts_readers_and_offers_the_sheet()
    {
        var people = Enumerable.Range(0, 41).Select(i => P($"k{i}", $"Person {i}", seen: i < 12 ? Since.AddHours(1) : null)).ToList();
        var s = MailReceiptView.Summarise(Rc(people));
        Assert.Equal(MailReceiptView.Kind.Group, s.Kind);
        Assert.Equal("Read by 12 of 41", s.Text);
        Assert.True(s.CanOpen);
        Assert.Equal(4, s.Faces.Count);           // avatar stack of readers
        Assert.All(s.Faces, f => Assert.NotNull(f.SeenAt));
    }

    [Fact]
    public void Single_recipient_shows_read_time_or_delivered()
    {
        var at = new DateTime(2026, 10, 14, 2, 12, 0, DateTimeKind.Utc);
        var read = MailReceiptView.Summarise(Rc([P("a", "Fildzah", seen: at)]));
        Assert.Equal(MailReceiptView.Kind.Single, read.Kind);
        Assert.Equal($"Read {MailReceiptView.ShortTime(at)}", read.Text);
        Assert.True(read.AllRead);

        var del = MailReceiptView.Summarise(Rc([P("a", "Fildzah")]));
        Assert.Equal("Delivered", del.Text);
        Assert.False(del.AllRead);
    }

    [Fact]
    public void External_only_untracked_and_counts_only_variants()
    {
        var ext = MailReceiptView.Summarise(Rc([], external: 1));
        Assert.Equal(MailReceiptView.Kind.External, ext.Kind);
        Assert.Equal("Read status not available · external address", ext.Text);
        Assert.False(ext.CanOpen);

        var old = MailReceiptView.Summarise(Rc([P("a", "A"), P("b", "B")], tracked: false));
        Assert.Equal(MailReceiptView.Kind.NotTracked, old.Kind);
        Assert.Equal($"Read status not tracked before {Since.ToLocalTime():d MMM}", old.Text);

        var mass = MailReceiptView.Summarise(Rc([], namesVisible: false, total: 1000, read: 412));
        Assert.Equal(MailReceiptView.Kind.CountsOnly, mass.Kind);
        Assert.Equal("Read by 412 of 1,000", mass.Text);
        Assert.Empty(mass.Faces);
        Assert.Equal(41, mass.Percent);
    }

    [Fact]
    public void Nothing_to_report_hides_the_row()
        => Assert.Equal(MailReceiptView.Kind.None, MailReceiptView.Summarise(Rc([])).Kind);

    [Theory]
    [InlineData(3, 3, "all")]
    [InlineData(3, 1, "part")]
    [InlineData(3, 0, "none")]
    public void Collapsed_thread_item_tick(int total, int read, string expected)
    {
        var people = Enumerable.Range(0, total).Select(i => P($"k{i}", $"P{i}", seen: i < read ? Since : null)).ToList();
        Assert.Equal(expected, MailReceiptView.Tick(Rc(people)));
        Assert.Null(MailReceiptView.Tick(Rc(people, tracked: false)));
    }

    // ───────────── sheet lists ─────────────

    [Fact]
    public void Sheet_splits_people_and_groups_by_read_state_and_searches_departments()
    {
        var people = new List<MailReceiptPerson>
        {
            P("me", "BAGASTYO", me: true, seen: Since.AddHours(2)),
            P("s", "Sulastri Basrewan", dept: "HCBP", seen: Since.AddHours(1)),
            P("r", "Rina Wulandari", field: "cc"),
            P("g1", "Ani", viaGroup: true, seen: Since.AddHours(3)),
            P("g2", "Budi", viaGroup: true),
            P("g3", "Citra", viaGroup: true),
        };
        var groups = new[] { new MailReceiptGroup("to", "shared", "ICT · SJA Karawang", 3, 1, ["g1", "g2", "g3"]) };
        var rc = Rc(people, groups);

        var read = MailReceiptView.Lists(rc, readTab: true, query: null);
        Assert.Equal(["Bagastyo (you)", "Sulastri Basrewan"], read.People.Select(MailReceiptView.DisplayName));
        var g = Assert.Single(read.Groups);
        Assert.Equal("1 of 3 read", g.Caption);
        Assert.Equal(["Ani"], g.Members.Select(m => m.Name));

        var notYet = MailReceiptView.Lists(rc, readTab: false, query: null);
        Assert.Equal(["Rina Wulandari"], notYet.People.Select(p => p.Name));
        Assert.Equal(["Budi", "Citra"], Assert.Single(notYet.Groups).Members.Select(m => m.Name));

        var hcbp = MailReceiptView.Lists(rc, readTab: true, query: "hcbp");
        Assert.Equal(["Sulastri Basrewan"], hcbp.People.Select(p => p.Name));
        Assert.Empty(hcbp.Groups);

        // A search matching a group member keeps the group (expanded) with just that member.
        var ani = MailReceiptView.Lists(rc, readTab: true, query: "ani");
        Assert.Equal(["Ani"], Assert.Single(ani.Groups).Members.Select(m => m.Name));
    }

    [Theory]
    [InlineData("RM BAGASTYO ANGGORO", "Rm Bagastyo Anggoro")]
    [InlineData("Fildzah Raudina", "Fildzah Raudina")]
    [InlineData("dewi anggraini", "Dewi Anggraini")]
    public void Names_are_title_cased(string raw, string expected) => Assert.Equal(expected, MailReceiptView.TitleCase(raw));

    [Fact]
    public void Read_stamp_is_local_time()
    {
        var at = new DateTime(2026, 10, 14, 2, 12, 0, DateTimeKind.Utc);
        Assert.Equal($"Read · {at.ToLocalTime():ddd d MMM, HH:mm}", MailReceiptView.ReadStamp(at));
    }

    // ───────────── components ─────────────

    private sealed class FakeApi(Func<HttpRequestMessage, object?> route) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            var body = route(request);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
                });
        }
    }

    private static (BunitContext Ctx, FakeApi Api) Ctx(Func<HttpRequestMessage, object?> route, bool phone = false)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<bool>("ipop.mobile.isMobile").SetResult(phone);
        var api = new FakeApi(route);
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new WebMail.MailService(auth, http));
        ctx.Services.AddSingleton<MailReceiptsStore>();
        ctx.Services.AddSingleton<MailReadingPrefs>();
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddScoped<PhotoViewerState>();
        return (ctx, api);
    }

    private static MailReceipts GroupMail(Guid id)
    {
        var people = new List<MailReceiptPerson>
        {
            P("me", "Bagastyo", me: true, seen: Since.AddHours(2)),
            P("s", "SULASTRI BASREWAN", dept: "HCBP", seen: Since.AddHours(1)),
            P("r", "Rina Wulandari", field: "cc"),
            P("g1", "Ani", viaGroup: true, seen: Since.AddHours(3)),
            P("g2", "Budi", viaGroup: true),
        };
        return Rc(people, [new MailReceiptGroup("to", "shared", "ICT · SJA Karawang", 2, 1, ["g1", "g2"])]) with { MailId = id };
    }

    [Fact]
    public void Receipts_row_opens_a_popover_with_tabs_search_and_footer()
    {
        var id = Guid.NewGuid();
        var (ctx, _) = Ctx(req => req.RequestUri!.PathAndQuery == $"/api/mail/{id}/receipts" ? GroupMail(id) : null);
        using var _c = ctx;
        var cut = ctx.Render<MailReceiptsRow>(p => p.Add(c => c.MailId, id));
        cut.WaitForAssertion(() => Assert.Contains("Read by 3 of 5", cut.Find(".mrr-seen").TextContent));
        Assert.Equal(3, cut.FindAll(".mrr-stk .pav").Count);

        cut.Find(".mrr-seen").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".mrr-pop")));
        Assert.Empty(cut.FindAll(".mrr-sheet"));
        var tabs = cut.FindAll(".mrr-seg button");
        Assert.Contains("Read by · 3", tabs[0].TextContent);
        Assert.Contains("Not yet · 2", tabs[1].TextContent);
        Assert.Contains("Sulastri Basrewan", cut.Markup);          // Title Case, never NIK
        Assert.Contains("HCBP · SJA Karawang", cut.Markup);
        Assert.Contains("Everyone on this mail sees the same list. Bcc recipients are never shown.", cut.Find(".mrr-foot").TextContent);
        Assert.Contains("1 of 2 read", cut.Markup);

        tabs[1].Click();
        cut.WaitForAssertion(() => Assert.Contains("Rina Wulandari", cut.Find(".mrr-list").TextContent));
        cut.Find(".mrr-srch input").Input("rina");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mrr-gr")));
    }

    [Fact]
    public void Receipts_row_uses_a_bottom_sheet_on_phones()
    {
        var id = Guid.NewGuid();
        var (ctx, _) = Ctx(req => req.RequestUri!.PathAndQuery == $"/api/mail/{id}/receipts" ? GroupMail(id) : null, phone: true);
        using var _c = ctx;
        var cut = ctx.Render<MailReceiptsRow>(p => p.Add(c => c.MailId, id));
        cut.WaitForAssertion(() => cut.Find(".mrr-seen").Click());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".mrr-sheet")));
        Assert.NotNull(cut.Find(".mrr-dim"));
    }

    [Fact]
    public void Mass_mail_receipts_show_the_count_only()
    {
        var id = Guid.NewGuid();
        var rc = Rc([], namesVisible: false, total: 1000, read: 412) with { MailId = id };
        var (ctx, _) = Ctx(req => req.RequestUri!.PathAndQuery == $"/api/mail/{id}/receipts" ? rc : null);
        using var _c = ctx;
        var cut = ctx.Render<MailReceiptsRow>(p => p.Add(c => c.MailId, id));
        cut.WaitForAssertion(() => Assert.Contains("Read by 412 of 1,000", cut.Find(".mrr-seen").TextContent));
        Assert.Empty(cut.FindAll(".mrr-stk"));
        Assert.Contains("names not shown", cut.Markup);
        cut.Find(".mrr-seen").Click();
        cut.WaitForAssertion(() => Assert.Contains("41%", cut.Find(".mrr-ring").TextContent));
        Assert.Empty(cut.FindAll(".mrr-seg"));
    }

    [Fact]
    public void Receipt_hint_refreshes_the_row()
    {
        var id = Guid.NewGuid();
        var read = 0;
        var (ctx, api) = Ctx(req =>
        {
            if (req.RequestUri!.PathAndQuery != $"/api/mail/{id}/receipts") return null;
            return Rc([P("a", "Fildzah", seen: read > 0 ? Since : null)]) with { MailId = id };
        });
        using var _c = ctx;
        var cut = ctx.Render<MailReceiptsRow>(p => p.Add(c => c.MailId, id));
        cut.WaitForAssertion(() => Assert.Contains("Delivered", cut.Find(".mrr-seen").TextContent));
        read = 1;
        ctx.Services.GetRequiredService<MailReceiptsStore>().OnHint(id);
        cut.WaitForAssertion(() => Assert.Contains("Read ", cut.Find(".mrr-seen").TextContent));
        Assert.Equal(2, api.Calls.Count(c => c.EndsWith("/receipts")));
    }

    [Fact]
    public void Untracked_mail_says_so_with_the_start_date()
    {
        var id = Guid.NewGuid();
        var (ctx, _) = Ctx(req => req.RequestUri!.PathAndQuery == $"/api/mail/{id}/receipts" ? Rc([P("a", "A")], tracked: false) with { MailId = id } : null);
        using var _c = ctx;
        var cut = ctx.Render<MailReceiptsRow>(p => p.Add(c => c.MailId, id));
        cut.WaitForAssertion(() => Assert.Contains($"Read status not tracked before {Since.ToLocalTime():d MMM}", cut.Markup));
        Assert.Empty(cut.FindAll("button.mrr-seen"));
    }

    // ───────────── recipients header ─────────────

    private static WebMail.MailRecipient R(string nik, string name, string type) => new(Guid.NewGuid(), name, nik, $"{nik}@x.co", type);

    private static MailDetail Detail(string sender, params WebMail.MailRecipient[] r) => new(
        Guid.NewGuid(), Guid.NewGuid(), sender, sender, "Webinar", "<p>x</p>", true, null,
        DateTime.UtcNow, null, null, 0, false, true, "inbox", null, null, r, [], []);

    [Fact]
    public void Header_puts_you_first_then_names_group_chips_and_more()
    {
        var d = Detail("S1", R("T1", "Sulastri Basrewan", "to"), R("T2", "Rikki Rinaldo", "to"), R(Me, "Bagastyo", "to"),
                R("T3", "Dewi Anggraini", "to"), R("T4", "Hendra Gunawan", "to"), R("C1", "Rina Wulandari", "cc"))
            with { RecipientTotal = 16, RecipientGroups = [new RecipientGroupCount("to", "shared", Guid.NewGuid(), "Plant · SJA Karawang", 10)] };
        var (ctx, _) = Ctx(_ => null);
        using var _c = ctx;
        var cut = ctx.Render<MailRecipientsHeader>(p => p.Add(c => c.Detail, d).Add(c => c.MyNik, Me));
        var to = cut.Find(".mrh-line[data-field=to]");
        Assert.StartsWith("you, Sulastri Basrewan, Rikki Rinaldo", to.QuerySelector(".mrh-names")!.TextContent.Trim());
        Assert.Contains("Plant · SJA Karawang", to.QuerySelector(".mrh-chip")!.TextContent);
        Assert.Equal("+12", to.QuerySelector(".mrh-more")!.TextContent.Trim());   // 2 hidden typed + 10 in the group
        var cc = cut.Find(".mrh-line[data-field=cc]");
        Assert.Equal("Rina Wulandari", cc.QuerySelector(".mrh-names")!.TextContent.Trim());
        Assert.Empty(cc.QuerySelectorAll(".mrh-more"));
        Assert.DoesNotContain(Me, cut.Markup);
    }

    [Fact]
    public void Header_more_opens_the_recipients_panel_with_people_and_groups_by_site()
    {
        var d = Detail("S1", R(Me, "Bagastyo", "to"), R("T1", "A1", "to"), R("T2", "A2", "to"), R("T3", "A3", "to"), R("C1", "Rina Wulandari", "cc"))
            with { RecipientTotal = 7, RecipientGroups = [new RecipientGroupCount("to", "shared", Guid.NewGuid(), "ICT · SJA Karawang", 2)] };
        var rc = Rc([
            P("me", "Bagastyo", me: true), P("t1", "A1"), P("t2", "A2"), P("t3", "A3"), P("c1", "Rina Wulandari", field: "cc", dept: "HCBP"),
            P("g1", "Ani", viaGroup: true), P("g2", "Budi", viaGroup: true, site: "SJA Sepanjang"),
        ], [new MailReceiptGroup("to", "shared", "ICT · SJA Karawang", 2, 0, ["g1", "g2"])]) with { MailId = d.Id };
        var (ctx, _) = Ctx(req => req.RequestUri!.PathAndQuery == $"/api/mail/{d.Id}/receipts" ? rc : null);
        using var _c = ctx;
        var cut = ctx.Render<MailRecipientsHeader>(p => p.Add(c => c.Detail, d).Add(c => c.MyNik, Me));
        cut.Find(".mrh-line[data-field=to] .mrh-more").Click();
        cut.WaitForAssertion(() => Assert.Contains("7 · To 6 · Cc 1", cut.Find(".mrh-panel .mrh-ph").TextContent));
        Assert.Equal(7, cut.FindAll(".mrh-ppl .mrh-pr").Count);
        Assert.Contains("You", cut.FindAll(".mrh-ppl .mrh-pr")[0].TextContent);
        Assert.Contains("Cc", cut.Markup);
        Assert.Contains("SJA Karawang", cut.Find(".mrh-site h5").TextContent);
        Assert.Equal("ICT", cut.Find(".mrh-site .mrh-gc b").TextContent.Trim());

        cut.Find(".mrh-panel .mrr-srch input").Input("rina");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mrh-ppl .mrh-pr")));
    }

    [Fact]
    public void Header_keeps_the_mass_mail_tail_for_counts_only_mail()
    {
        var d = Detail("S1", R(Me, "Bagastyo", "to"))
            with { RecipientTotal = 1000, IsMass = true, RecipientGroups = [new RecipientGroupCount("to", "shared", Guid.NewGuid(), "All employees · PT Santos Jaya Abadi", 1000)] };
        var (ctx, _) = Ctx(_ => null);
        using var _c = ctx;
        var cut = ctx.Render<MailRecipientsHeader>(p => p.Add(c => c.Detail, d).Add(c => c.MyNik, Me));
        var to = cut.Find(".mrh-line[data-field=to]");
        Assert.Contains("All employees · PT Santos Jaya Abadi", to.TextContent);
        Assert.Contains("1,000", to.TextContent);
    }

    [Fact]
    public void Header_shows_bcc_to_the_sender_only()
    {
        var d = Detail(Me, R("T1", "Fildzah", "to"), R("B1", "Yudi Arta", "bcc"));
        var (ctx, _) = Ctx(_ => null);
        using var _c = ctx;
        var cut = ctx.Render<MailRecipientsHeader>(p => p.Add(c => c.Detail, d).Add(c => c.MyNik, Me));
        Assert.Contains("Only you can see this", cut.Find(".mrh-line[data-field=bcc]").TextContent);

        var other = Detail("S1", R(Me, "Bagastyo", "to"));
        var cut2 = ctx.Render<MailRecipientsHeader>(p => p.Add(c => c.Detail, other).Add(c => c.MyNik, Me));
        Assert.Empty(cut2.FindAll(".mrh-line[data-field=bcc]"));
    }

    [Fact]
    public void Header_hides_the_list_when_recipients_are_undisclosed()
    {
        var d = Detail("S1", R(Me, "Bagastyo", "to")) with { HideRecipients = true, RecipientTotal = 30 };
        var (ctx, _) = Ctx(_ => null);
        using var _c = ctx;
        var cut = ctx.Render<MailRecipientsHeader>(p => p.Add(c => c.Detail, d).Add(c => c.MyNik, Me));
        Assert.Contains("undisclosed recipients", cut.Markup);
        Assert.Empty(cut.FindAll(".mrh-more"));
    }

    // ───────────── EPop list marker ─────────────

    [Fact]
    public void Epop_label_becomes_an_icon_not_a_row_chip()
    {
        Assert.True(MailEpop.IsImported(["SGM", "Arsip EPop"]));
        Assert.False(MailEpop.IsImported(["SGM"]));
        Assert.False(MailEpop.IsImported(null));
        Assert.Equal(["SGM"], MailEpop.RowLabels(["Arsip EPop", "SGM"]));

        var page = File.ReadAllText(Path.Combine(Root(), "src/IPop.Web/Pages/Mail.razor"));
        Assert.Contains("MailEpop.RowLabels(item.Labels)", page);
        Assert.Contains("Imported from EPop", page);
    }

    // ───────────── mass panel ─────────────

    [Fact]
    public void Mass_panel_reads_counts_and_names_from_receipts_not_the_unread_badge()
    {
        var d = Detail(Me, R("T1", "A", "to")) with { IsMass = true, RecipientTotal = 3 };
        var rc = Rc([P("a", "ANI", seen: Since), P("b", "Budi"), P("c", "Citra", delivered: false)], isSender: true) with { MailId = d.Id };
        var (ctx, api) = Ctx(req => req.RequestUri!.PathAndQuery switch
        {
            var u when u == $"/api/mail/{d.Id}/receipts" => rc,
            var u when u == $"/api/mail/{d.Id}/stats" => new MailStats(3, 3, 3, []),   // badge reads: must be ignored
            _ => null,
        });
        using var _c = ctx;
        var cut = ctx.Render<MailMassPanel>(p => p.Add(c => c.Detail, d).Add(c => c.IsSender, true));
        cut.WaitForAssertion(() => Assert.Contains("Ani", cut.Find(".mm-list").TextContent));
        var head = cut.Find(".mm-stats-h").TextContent;
        Assert.Contains("1read", head.Replace(" ", "").Replace("\n", ""));
        Assert.Contains("2delivered", head.Replace(" ", "").Replace("\n", ""));
        Assert.DoesNotContain(api.Calls, c => c.Contains("/readers"));
        cut.FindAll(".mm-tabs button")[1].Click();
        cut.WaitForAssertion(() => Assert.Contains("Budi", cut.Find(".mm-list").TextContent));
    }

    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IPop.slnx"))) root = root.Parent;
        return root!.FullName;
    }
}
