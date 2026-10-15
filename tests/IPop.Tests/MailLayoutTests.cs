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

/// Outlook-style Mail layout (mockup docs/mockups/mail-layout-outlook.html v3, approved 2026-10-15):
/// one reply surface (toolbar pills + quick reply under the newest message), newest-first thread with
/// "Earlier · N" one-line rows, compact header with Details table, label chips, list density, resizable panes.
public class MailLayoutTests
{
    private const string Me = "ME01";

    private static WebMail.MailRecipient R(string nik, string name, string type, string? email = null)
        => new(Guid.NewGuid(), name, nik, email ?? $"{name.Split(' ')[0].ToLowerInvariant()}@santos.co.id", type);

    private static MailDetail Mail(string senderNik, string senderName, DateTime sent, params WebMail.MailRecipient[] r) => new(
        Guid.NewGuid(), Guid.NewGuid(), senderName, senderNik, "Halo Saya Jason", $"<p>from {senderName}</p>", true, null,
        sent, null, null, 0, false, true, "inbox", null, null, r, [], []);

    private static MailSummary Sum(MailDetail d, string snip) => new(d.Id, d.SenderId, d.SenderName, d.SenderNik, d.Subject, snip, d.SentAt,
        true, false, false, d.ThreadCount, d.ThreadRootId is null, d.ThreadRootId);

    private sealed class FakeApi(Func<string, object?> route) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            var body = route(request.RequestUri!.PathAndQuery);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
                });
        }
    }

    private static BunitContext Ctx(Func<string, object?> route)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<bool>("ipop.mobile.isMobile").SetResult(false);
        var http = new HttpClient(new FakeApi(route)) { BaseAddress = new Uri("http://localhost/") };
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

    /// Jason's thread: Jason (oldest, Tue) → Kevin (Tue) → Jason (newest, Thu).
    private static (MailDetail Oldest, MailDetail Mid, MailDetail Newest, BunitContext Ctx) Thread()
    {
        var t0 = new DateTime(2026, 10, 13, 8, 48, 0, DateTimeKind.Utc);
        var oldest = Mail("J1", "JASON KRISTANTO", t0, R(Me, "Bagastyo", "to"), R("K1", "Kevin Austin Widjaja", "to")) with { ThreadCount = 2 };
        var mid = Mail("K1", "Kevin Austin Widjaja", t0.AddMinutes(14), R("J1", "JASON KRISTANTO", "to"), R(Me, "Bagastyo", "cc"))
            with { ThreadRootId = oldest.Id, InReplyToId = oldest.Id };
        var newest = Mail("J1", "JASON KRISTANTO", t0.AddDays(2), R(Me, "Bagastyo", "to"), R("K1", "Kevin Austin Widjaja", "to"),
                R("A1", "Alphin Santoso", "cc"), R("F1", "Fildzah Raudina", "cc"), R("Y1", "Yulia Kartika", "cc"), R("D1", "Dimas Prakoso", "cc"))
            with { ThreadRootId = oldest.Id, InReplyToId = mid.Id };
        var ctx = Ctx(url =>
            url.StartsWith($"/api/mail/threads/{oldest.Id}") ? new MailPaged<MailSummary>([Sum(newest, "Perkenalkan"), Sum(mid, "Halo Jason, salam kenal!"), Sum(oldest, "Halo semua")], 3, 1, 50)
            : url == $"/api/mail/{oldest.Id}" ? oldest
            : url == $"/api/mail/{mid.Id}" ? mid
            : url == $"/api/mail/{newest.Id}" ? newest
            : null);
        return (oldest, mid, newest, ctx);
    }

    // ───────────── thread order ─────────────

    [Fact]
    public void Thread_shows_newest_on_top_expanded_then_earlier_rows_down_to_the_oldest()
    {
        var (oldest, mid, newest, ctx) = Thread();
        using var _ = ctx;
        var cut = ctx.Render<MailThreadStack>(p => p.Add(c => c.Detail, newest).Add(c => c.MyNik, Me));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".thr-msg").Count));

        var items = cut.FindAll(".thr-msg");
        Assert.Equal(newest.Id.ToString(), items[0].GetAttribute("data-thr-id"));
        Assert.Contains("newest", items[0].ClassList);
        Assert.Contains("open", items[0].ClassList);
        Assert.Equal(mid.Id.ToString(), items[1].GetAttribute("data-thr-id"));
        Assert.Equal(oldest.Id.ToString(), items[2].GetAttribute("data-thr-id"));   // oldest at the very bottom
        Assert.All(items.Skip(1), i => Assert.Contains("collapsed", i.ClassList));
        Assert.Equal("Earlier · 2 messages", cut.Find(".thr-earlier").TextContent.Trim());
        // Older rows are one line: name + snippet, no body.
        Assert.Contains("Halo Jason, salam kenal!", items[1].QuerySelector(".thr-snip")!.TextContent);
        Assert.Null(items[1].QuerySelector(".thr-body"));
        // No "This message" badge any more.
        Assert.DoesNotContain("This message", cut.Markup);
    }

    [Fact]
    public void Clicking_an_earlier_row_expands_it_in_place()
    {
        var (_, mid, newest, ctx) = Thread();
        using var _c = ctx;
        var cut = ctx.Render<MailThreadStack>(p => p.Add(c => c.Detail, newest).Add(c => c.MyNik, Me));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".thr-msg").Count));
        cut.Find($".thr-msg[data-thr-id=\"{mid.Id}\"] .thr-head").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find($".thr-msg[data-thr-id=\"{mid.Id}\"] .thr-body .mrd-body")));
        Assert.Equal(mid.Id.ToString(), cut.FindAll(".thr-msg")[1].GetAttribute("data-thr-id"));   // same place
        Assert.Contains("from Kevin Austin Widjaja", cut.Find($".thr-msg[data-thr-id=\"{mid.Id}\"] .thr-body").TextContent);
    }

    [Fact]
    public void A_single_mail_renders_through_the_same_stack_without_an_earlier_divider()
    {
        var solo = Mail("S1", "Sender", DateTime.UtcNow, R(Me, "Bagastyo", "to"));
        using var ctx = Ctx(_ => null);
        var cut = ctx.Render<MailThreadStack>(p => p.Add(c => c.Detail, solo).Add(c => c.MyNik, Me));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".thr-msg.newest.open")));
        Assert.Empty(cut.FindAll(".thr-earlier"));
        Assert.NotNull(cut.Find(".mqr"));
    }

    // ───────────── one reply surface ─────────────

    [Fact]
    public void Open_messages_have_small_reply_and_more_icons_not_a_button_row()
    {
        var (_, mid, newest, ctx) = Thread();
        using var _c = ctx;
        MailDetail? forwarded = null;
        var cut = ctx.Render<MailThreadStack>(p => p.Add(c => c.Detail, newest).Add(c => c.MyNik, Me).Add(c => c.OnForward, d => forwarded = d));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".thr-msg").Count));
        Assert.Empty(cut.FindAll(".thr-acts"));
        Assert.Empty(cut.FindAll(".comp-btn:not(.thr-older)"));

        var head = cut.Find($".thr-msg[data-thr-id=\"{newest.Id}\"] .thr-sndr");
        Assert.NotNull(head.QuerySelector("button.thr-ib[aria-label=\"Reply to this message\"]"));
        head.QuerySelector("button.thr-ib[aria-label=\"More actions for this message\"]")!.Click();
        var menu = cut.Find($".thr-msg[data-thr-id=\"{newest.Id}\"] [role=menu]");
        var labels = menu.QuerySelectorAll("[role=menuitem]").Select(b => b.TextContent.Trim()).ToList();
        Assert.Equal(["Reply", "Reply all", "Forward"], labels.Take(3));
        menu.QuerySelectorAll("[role=menuitem]").Single(b => b.TextContent.Trim() == "Forward").Click();
        Assert.Equal(newest.Id, forwarded?.Id);
    }

    [Fact]
    public void Quick_reply_is_a_40px_box_directly_under_the_newest_message()
    {
        var (_, _, newest, ctx) = Thread();
        using var _c = ctx;
        var cut = ctx.Render<MailThreadStack>(p => p.Add(c => c.Detail, newest).Add(c => c.MyNik, Me));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".thr-msg").Count));
        var newestEl = cut.Find(".thr-msg.newest");
        Assert.Equal("mqr", newestEl.NextElementSibling!.ClassName?.Split(' ')[0]);
        Assert.Equal("Reply to Jason Kristanto…", cut.Find("button.mqr-in .mqr-ph").TextContent.Trim());
        Assert.Empty(cut.FindAll("textarea"));
    }

    [Fact]
    public void Quick_reply_expands_inline_switches_reply_all_sends_and_collapses_on_escape()
    {
        var (_, _, newest, ctx) = Thread();
        using var _c = ctx;
        MailReplyStart? sent = null, full = null;
        var cut = ctx.Render<MailThreadStack>(p => p.Add(c => c.Detail, newest).Add(c => c.MyNik, Me)
            .Add(c => c.QuickSend, s => { sent = s; return Task.FromResult(true); })
            .Add(c => c.OnOpenFull, s => full = s));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".thr-msg").Count));

        cut.Find("button.mqr-in").Click();
        var box = cut.Find("#mail-quick-reply");
        Assert.NotNull(box.QuerySelector("textarea#mail-quick-reply-text"));
        Assert.NotNull(box.QuerySelector("button[aria-label=Emoji]"));
        Assert.NotNull(box.QuerySelector("button[aria-label=Attach]"));
        Assert.Contains("Open full editor", box.TextContent);
        Assert.Contains("Jason Kristanto", box.QuerySelector(".mqr-to")!.TextContent);

        cut.Find("button.mqr-mode").Click();                       // Reply → Reply all
        Assert.Contains("Reply all", cut.Find("button.mqr-mode").TextContent);
        Assert.Contains("Kevin Austin Widjaja", cut.Find(".mqr-to").TextContent);

        cut.Find("textarea#mail-quick-reply-text").Input("Halo Jason");
        cut.Find("button.mqr-send").Click();
        cut.WaitForAssertion(() => Assert.NotNull(sent));
        Assert.Equal(newest.Id, sent!.Target.Id);
        Assert.Equal("Halo Jason", sent.Text);
        Assert.True(sent.All);
        Assert.Empty(sent.Attachments);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("textarea")));   // sent → back to the 40 px box

        cut.Find("button.mqr-in").Click();
        cut.Find("textarea#mail-quick-reply-text").Input("draft");
        cut.Find("button.mqr-open").Click();
        Assert.Equal(newest.Id, full?.Target.Id);
        Assert.Equal("draft", full?.Text);

        cut.Find("button.mqr-in").Click();
        cut.Find("#mail-quick-reply").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("textarea")));
    }

    [Fact]
    public void Reply_icon_on_an_earlier_message_targets_that_message()
    {
        var (_, mid, newest, ctx) = Thread();
        using var _c = ctx;
        MailReplyStart? sent = null;
        var cut = ctx.Render<MailThreadStack>(p => p.Add(c => c.Detail, newest).Add(c => c.MyNik, Me)
            .Add(c => c.QuickSend, s => { sent = s; return Task.FromResult(true); }));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".thr-msg").Count));
        cut.Find($".thr-msg[data-thr-id=\"{mid.Id}\"] button.thr-ib[aria-label=\"Reply to this message\"]").Click();
        cut.WaitForAssertion(() => Assert.Contains("Kevin Austin Widjaja", cut.Find(".mqr-to").TextContent));
        cut.Find("textarea#mail-quick-reply-text").Input("ok");
        cut.Find("button.mqr-send").Click();
        cut.WaitForAssertion(() => Assert.Equal(mid.Id, sent?.Target.Id));
        Assert.False(sent!.All);
    }

    [Fact]
    public void Parent_can_open_quick_reply_from_the_toolbar_in_either_mode()
    {
        var (_, _, newest, ctx) = Thread();
        using var _c = ctx;
        var cut = ctx.Render<MailThreadStack>(p => p.Add(c => c.Detail, newest).Add(c => c.MyNik, Me));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".thr-msg").Count));
        cut.InvokeAsync(() => cut.Instance.OpenQuickReplyAsync(null, all: true));
        cut.WaitForAssertion(() => Assert.Contains("Reply all", cut.Find("button.mqr-mode").TextContent));
        Assert.Equal(newest.Id, cut.Instance.Newest.Id);
    }

    // ───────────── compact header + Details ─────────────

    [Fact]
    public void Header_is_one_sender_line_and_one_recipients_line_with_details_collapsed()
    {
        var (_, _, newest, ctx) = Thread();
        using var _c = ctx;
        var cut = ctx.Render<MailThreadStack>(p => p.Add(c => c.Detail, newest).Add(c => c.MyNik, Me));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".thr-msg").Count));
        var top = cut.Find(".thr-msg.newest");
        Assert.Equal("Jason Kristanto", top.QuerySelector(".thr-name")!.TextContent.Trim());
        var line = top.QuerySelector(".thr-rl")!.TextContent;
        Assert.Contains("To you, Kevin Austin Widjaja", line);
        Assert.Contains("Cc Alphin Santoso", line);
        Assert.Contains("+3", line);   // the existing recipients panel opens from it
        Assert.NotNull(top.QuerySelector(".thr-rl button.mrh-tmore"));
        var toggle = top.QuerySelector("button.thr-dtg")!;
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Empty(top.QuerySelectorAll(".mdt-row"));

        toggle.Click();
        top = cut.Find(".thr-msg.newest");
        Assert.Equal("true", top.QuerySelector("button.thr-dtg")!.GetAttribute("aria-expanded"));
        var keys = top.QuerySelectorAll(".mdt-row .mdt-k").Select(k => k.TextContent.Trim()).ToList();
        Assert.Equal(["From", "To", "Cc", "Date"], keys);
        Assert.Contains("you", top.QuerySelector(".mdt-pill.you")!.TextContent);
        // 4 Cc people: 4 pills, no +N; NIK never rendered.
        Assert.Equal(4, top.QuerySelectorAll(".mdt-row[data-k=cc] .mdt-pill").Length);
        Assert.DoesNotContain(">A1<", cut.Markup);
        Assert.DoesNotContain(Me, cut.Markup);
    }

    [Fact]
    public void Details_lists_bcc_only_to_the_sender_and_folds_long_lists_behind_more()
    {
        var people = Enumerable.Range(1, 7).Select(i => R($"T{i}", $"Person {i}", "to")).ToList();
        var mine = Mail(Me, "Bagastyo", DateTime.UtcNow, [.. people, R("B1", "Hidden Person", "bcc")]);
        using var ctx = Ctx(_ => null);
        var cut = ctx.Render<MailDetailsTable>(p => p.Add(c => c.Mail, mine).Add(c => c.MyNik, Me));
        Assert.Equal(["From", "To", "Bcc", "Date"], cut.FindAll(".mdt-k").Select(k => k.TextContent.Trim()));
        Assert.Equal(4, cut.FindAll(".mdt-row[data-k=to] .mdt-pill").Count);
        cut.Find(".mdt-row[data-k=to] button.mdt-more").Click();
        Assert.Equal(7, cut.FindAll(".mdt-row[data-k=to] .mdt-pill").Count);

        var theirs = Mail("S1", "Sender", DateTime.UtcNow, R(Me, "Bagastyo", "to"), R("B1", "Hidden Person", "bcc"));
        var cut2 = ctx.Render<MailDetailsTable>(p => p.Add(c => c.Mail, theirs).Add(c => c.MyNik, Me));
        Assert.DoesNotContain("Bcc", cut2.Markup);
        Assert.DoesNotContain("Hidden Person", cut2.Markup);
    }

    [Fact]
    public void Sender_email_shows_only_when_it_does_not_carry_the_id()
    {
        var ok = Mail("S1", "Kevin", DateTime.UtcNow, R("S1", "Kevin", "from", "kevin.austin@santos.co.id"), R(Me, "Bagastyo", "to"));
        Assert.Equal("kevin.austin@santos.co.id", MailRecipientLine.SenderEmail(ok));
        var leak = Mail("EPOP-12", "Payroll", DateTime.UtcNow, R("EPOP-12", "Payroll", "from", "epop-12@santos.co.id"));
        Assert.Null(MailRecipientLine.SenderEmail(leak));
        Assert.Null(MailRecipientLine.SenderEmail(Mail("S1", "X", DateTime.UtcNow)));
    }

    [Fact]
    public void Compact_recipients_line_reads_to_two_names_then_cc_one_name_and_a_count()
    {
        var (_, _, newest, ctx) = Thread();
        ctx.Dispose();
        var parts = MailRecipientLine.Compact(newest, Me);
        Assert.Equal("To", parts[0].Label);
        Assert.Equal("you, Kevin Austin Widjaja", parts[0].Names);
        Assert.Equal(0, parts[0].More);
        Assert.Equal("Cc", parts[1].Label);
        Assert.Equal("Alphin Santoso", parts[1].Names);
        Assert.Equal(3, parts[1].More);

        var hidden = newest with { HideRecipients = true };
        var h = Assert.Single(MailRecipientLine.Compact(hidden, Me));
        Assert.Equal("undisclosed recipients", h.Names);
    }

    // ───────────── addressing: Reply all from the quick reply ─────────────

    [Fact]
    public void Quick_reply_all_copies_everyone_but_me_and_keeps_cc()
    {
        var d = Mail("S1", "Sender", DateTime.UtcNow, R(Me, "Me", "to"), R("T2", "Tini", "to"), R("C1", "Cici", "cc"), R("B1", "Hidden", "bcc"));
        var one = MailAddressing.QuickReply(d, "x", Me);
        Assert.Equal(["S1"], one.To);
        Assert.Empty(one.Cc);
        var all = MailAddressing.QuickReply(d, "x", Me, all: true);
        Assert.Equal(["S1", "T2"], all.To);
        Assert.Equal(["C1"], all.Cc);
        var mass = MailAddressing.QuickReply(d with { CanReplyAll = false }, "x", Me, all: true);
        Assert.Equal(["S1"], mass.To);
        Assert.Empty(mass.Cc);
    }

    // ───────────── list rows: density + label chips ─────────────

    private static MailSummary Row(params string[] labels) => new(Guid.NewGuid(), Guid.NewGuid(), "Human Capital", "HC1",
        "Undangan Sosialisasi Kebijakan Terbaru", "Dear All, dalam rangka…", DateTime.UtcNow, true, true, true, 3, true, null, Labels: labels);

    private static IRenderedComponent<MailListRow> RenderRow(BunitContext ctx, MailSummary m, bool comfortable = false)
        => ctx.Render<MailListRow>(p => p.Add(c => c.Item, m).Add(c => c.Comfortable, comfortable)
            .Add(c => c.LabelColor, n => n == "HC" ? "#ff9f0a" : "#0a84ff"));

    [Fact]
    public void Compact_rows_are_two_lines_and_comfortable_rows_three()
    {
        using var ctx = Ctx(_ => null);
        var compact = RenderRow(ctx, Row("HC"));
        Assert.Equal(2, compact.FindAll(".mlr-line").Count);
        Assert.Contains("Dear All", compact.Find(".mlr-l2 .mlr-tx").TextContent);   // subject — preview on one line
        var comfy = RenderRow(ctx, Row("HC"), comfortable: true);
        Assert.Equal(3, comfy.FindAll(".mlr-line").Count);
        Assert.Contains("Dear All", comfy.Find(".mlr-l3 .mlr-tx").TextContent);
        Assert.Contains("mlr-comfy", comfy.Find(".mlr").ClassList);
    }

    [Fact]
    public void Rows_show_two_label_chips_then_more_and_epop_is_an_icon_not_a_chip()
    {
        using var ctx = Ctx(_ => null);
        var cut = RenderRow(ctx, Row("HC", "SGM", "ICT", MailEpop.Label));
        var chips = cut.FindAll(".mlr-chip");
        Assert.Equal(["HC", "SGM", "+1"], chips.Select(c => c.TextContent.Trim()));
        Assert.Contains("k1", chips[0].ClassList);                  // the first label: always readable
        Assert.Contains("--c:#ff9f0a", chips[0].GetAttribute("style"));
        Assert.Equal("ICT", chips[2].GetAttribute("title"));
        Assert.DoesNotContain(MailEpop.Label, string.Join("|", chips.Select(c => c.TextContent)));
        Assert.NotNull(cut.Find(".mlr-arc[title=\"Imported from EPop\"]"));
        Assert.Equal("3", cut.Find(".mlr-cnt").TextContent.Trim());
    }

    [Fact]
    public void Narrow_list_keeps_the_first_label_and_truncates_the_text_instead()
    {
        var css = Read("src/IPop.Web/wwwroot/css/v6/mail-layout.css");
        Assert.Matches(@"flex:\s*none", Rule(css, ".mlr-chips"));
        Assert.Matches(@"text-overflow:\s*ellipsis", Rule(css, ".mlr-tx"));
        Assert.Matches(@"min-width:\s*0", Rule(css, ".mlr-tx"));
        // Under the narrow container query only the second chip goes; the first never hides.
        var narrow = string.Join("\n", Regex.Matches(css, @"@container mlist \(max-width:\s*\d+px\)\s*\{(.*?)\}\s*\}", RegexOptions.Singleline).Select(m => m.Groups[1].Value));
        Assert.Contains(".mlr-chip.k2", narrow);
        Assert.DoesNotContain(".k1", narrow);
    }

    [Theory]
    [InlineData(null, "compact")]
    [InlineData("", "compact")]
    [InlineData("weird", "compact")]
    [InlineData("comfortable", "comfortable")]
    [InlineData("compact", "compact")]
    public void Density_setting_defaults_to_compact(string? stored, string expected)
        => Assert.Equal(expected, MailListDensity.Parse(stored));

    // ───────────── page wiring ─────────────

    [Fact]
    public void Page_has_one_reply_surface_resizable_panes_and_the_new_mail_button()
    {
        var page = Read("src/IPop.Web/Pages/Mail.razor");
        Assert.DoesNotContain("Quick Reply", page);
        Assert.DoesNotContain("btn-primary-reply", page);
        Assert.DoesNotContain("inline-reply-box", page);
        foreach (var pill in new[] { ">Reply<", ">Reply all<", ">Forward<" }) Assert.Contains(pill, page);
        Assert.Equal(2, Regex.Matches(page, "data-mlx-split=\"[12]\"").Count);
        Assert.Equal(2, Regex.Matches(page, "data-mlx-split=\"[12]\" role=\"separator\" aria-orientation=\"vertical\"").Count);
        Assert.Contains("New mail", page);
        Assert.Contains("data-mlx-act=\"collapse\"", page);
        Assert.Contains("data-mlx-act=\"expand\"", page);
        Assert.Contains("data-mlx-act=\"reset\"", page);
        Assert.Contains("ipopMailLayout.bind", page);
        Assert.Contains("MailListDensity", page);
        Assert.Contains("<MailListRow", page);
        Assert.Contains("<MailThreadStack", page);
        var index = Read("src/IPop.Web/wwwroot/index.html");
        Assert.Contains("js/mail-layout.js", index);
        Assert.Contains("css/v6/mail-layout.css", index);
    }

    [Fact]
    public void Scrollbars_are_thin_rounded_without_arrows_and_firefox_gets_the_standard_properties()
    {
        var css = Read("src/IPop.Web/wwwroot/css/components/third-party-overrides.css");
        Assert.Matches(@"--app-scrollbar-size:\s*8px", css);
        Assert.Matches(@"::-webkit-scrollbar-button[^{]*\{\s*display:\s*none", css);
        // Chrome ignores ::-webkit-scrollbar once scrollbar-width/color are set, so those go to Firefox only.
        Assert.Matches(@"@supports \(-moz-appearance:\s*none\)\s*\{\s*\*\s*\{\s*scrollbar-width:\s*thin;", css);
        var mail = Read("src/IPop.Web/wwwroot/css/v6/mail-layout.css");
        Assert.Contains("[data-mlx-scrolling]", mail);
    }

    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IPop.slnx"))) root = root.Parent;
        return root!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

    private static string Rule(string css, string selector)
        => Regex.Match(css, Regex.Escape(selector) + @"\s*\{([^}]*)\}").Groups[1].Value;
}
