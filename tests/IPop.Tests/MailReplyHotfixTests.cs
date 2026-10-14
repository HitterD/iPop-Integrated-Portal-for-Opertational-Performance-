using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using IPop.Application.Mail;
using IPop.Web.Components.Mail;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using WebMail = IPop.Web.Services;

namespace IPop.Tests;

/// Hotfix 2026-10-14: per-message Reply / Reply all / Forward / Quick reply, reply addressing,
/// and the Cc names that were listed twice in the reading-pane header.
public class MailReplyHotfixTests
{
    private const string Me = "ME01";

    private static WebMail.MailRecipient R(string nik, string name, string type) => new(Guid.NewGuid(), name, nik, $"{nik}@x.co", type);

    private static MailDetail Mail(string senderNik, string senderName, params WebMail.MailRecipient[] recipients) => new(
        Guid.NewGuid(), Guid.NewGuid(), senderName, senderNik, "Budget", "<p>body</p>", true, null,
        DateTime.UtcNow, null, null, 0, false, true, "inbox", null, null,
        recipients, [], []);

    // ---- header text ----

    [Fact]
    public void Header_lists_to_and_cc_names_once_each()
    {
        var d = Mail("S1", "Sender", R(Me, "Me", "to"), R("C1", "Cici", "cc"), R("C2", "Dodi", "cc"), R("B1", "Hidden", "bcc"));
        Assert.Equal("Me", MailAddressing.ToNames(d));
        Assert.Equal("Cici, Dodi", MailAddressing.CcNames(d));
    }

    // ---- reply addressing ----

    private static string[] Niks(IEnumerable<MemberSearchRow> rows) => rows.Select(r => r.Nik).ToArray();

    [Fact]
    public void Reply_to_someone_elses_mail_goes_to_the_sender()
    {
        var d = Mail("S1", "Sender", R(Me, "Me", "to"), R("C1", "Cici", "cc"));
        var p = MailAddressing.ForReply(d, Me, all: false);
        Assert.Equal(["S1"], Niks(p.To));
        Assert.Empty(p.Cc);
        Assert.Null(p.Notice);
    }

    [Fact]
    public void Reply_to_my_own_sent_mail_goes_to_its_recipients_not_to_me()
    {
        var d = Mail(Me, "Me", R("T1", "Tono", "to"), R("T2", "Tini", "to"), R("C1", "Cici", "cc"), R("B1", "Hidden", "bcc"), R(Me, "Me", "from"));
        var p = MailAddressing.ForReply(d, Me, all: false);
        Assert.Equal(["T1", "T2"], Niks(p.To));
        Assert.Empty(p.Cc);

        var all = MailAddressing.ForReply(d, Me, all: true);
        Assert.Equal(["T1", "T2"], Niks(all.To));
        Assert.Equal(["C1"], Niks(all.Cc));   // never the Bcc, never me
    }

    [Fact]
    public void Reply_to_my_own_mail_sent_only_as_cc_uses_the_cc_people()
    {
        var d = Mail(Me, "Me", R("C1", "Cici", "cc"));
        Assert.Equal(["C1"], Niks(MailAddressing.ForReply(d, Me, all: false).To));
    }

    [Fact]
    public void Reply_all_to_someone_elses_mail_keeps_everyone_but_me_without_duplicates()
    {
        var d = Mail("S1", "Sender", R(Me, "Me", "to"), R("T2", "Tini", "to"), R("S1", "Sender", "to"),
            R("C1", "Cici", "cc"), R(me(), "Me", "cc"), R("T2", "Tini", "cc"), R("B1", "Hidden", "bcc"));
        var p = MailAddressing.ForReply(d, Me, all: true);
        Assert.Equal(["S1", "T2"], Niks(p.To));
        Assert.Equal(["C1"], Niks(p.Cc));
        static string me() => Me.ToLowerInvariant();   // NIK match is case-insensitive
    }

    [Fact]
    public void Reply_to_a_sender_without_an_active_account_leaves_to_empty_and_says_why()
    {
        var inactive = Mail("EPOP-ABC", "Payroll Staff", R(Me, "Me", "to")) with { SenderIsActive = false };

        var p = MailAddressing.ForReply(inactive, Me, all: false);
        Assert.Empty(p.To);
        Assert.NotNull(p.Notice);
        Assert.Contains("Payroll Staff", p.Notice);
        Assert.DoesNotContain("EPOP-ABC", p.Notice);   // never show an id
    }

    [Fact]
    public void Quick_reply_request_targets_the_chosen_message_and_its_people()
    {
        var root = Mail(Me, "Me", R("T1", "Tono", "to"));
        var older = root with { Subject = "Re: Budget" };
        var req = MailAddressing.QuickReply(older, "  ok\nthanks ", Me);
        Assert.Equal(older.Id, req.InReplyToId);
        Assert.Equal(older.Id, req.ThreadRootId);           // a root is its own thread key
        Assert.Equal(["T1"], req.To);                       // own mail → its recipients, not me
        Assert.Equal("Re: Budget", req.Subject);            // no "Re: Re:"
        Assert.StartsWith("<p>ok<br/>thanks</p>", req.Body);
        Assert.Contains("mail-quote", req.Body);

        var reply = Mail("S1", "Sender", R(Me, "Me", "to")) with { ThreadRootId = root.Id };
        var r2 = MailAddressing.QuickReply(reply, "x", Me);
        Assert.Equal(root.Id, r2.ThreadRootId);
        Assert.Equal(["S1"], r2.To);
        Assert.Equal("Re: Budget", r2.Subject);
    }

    // ---- server: the reader learns whether the sender can still receive mail ----

    [Fact]
    public async Task Detail_says_when_the_sender_has_no_active_account()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var sent = await new IPop.Infrastructure.Mail.MailService(t.Verify()).SendAsync(a.Id, new ComposeMailRequest("Hi", "<p>x</p>", [b.Nik], [], []), default);
        var before = await new IPop.Infrastructure.Mail.MailService(t.Verify()).GetAsync(b.Id, sent.MailId!.Value, default);
        Assert.True(before!.SenderIsActive);

        var db = t.Verify();
        db.Users.Single(u => u.Id == a.Id).IsActive = false;
        db.SaveChanges();
        var after = await new IPop.Infrastructure.Mail.MailService(t.Verify()).GetAsync(b.Id, sent.MailId.Value, default);
        Assert.False(after!.SenderIsActive);
    }

    // ---- thread stack: every message offers the actions ----

    private sealed class FakeApi(Func<string, object?> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = route(request.RequestUri!.PathAndQuery);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
                });
        }
    }

    private static MailSummary Sum(MailDetail d) => new(d.Id, d.SenderId, d.SenderName, d.SenderNik, d.Subject, "snip", d.SentAt,
        true, false, false, d.ThreadCount, d.ThreadRootId is null, d.ThreadRootId);

    private static BunitContext Ctx(Func<string, object?> route)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new FakeApi(route)) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new WebMail.MailService(auth, http));
        ctx.Services.AddSingleton<IPop.Web.Services.MailReceiptsStore>();
        ctx.Services.AddSingleton<IPop.Web.Services.MailReadingPrefs>();
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }

    [Fact]
    public void Every_opened_message_in_a_thread_offers_reply_reply_all_forward_and_quick_reply()
    {
        var root = Mail(Me, "Me", R("T1", "Tono", "to"), R("C1", "Cici", "cc")) with { ThreadCount = 1 };
        var reply = Mail("T1", "Tono", R(Me, "Me", "to"), R("C1", "Cici", "cc")) with { ThreadRootId = root.Id, InReplyToId = root.Id };
        using var ctx = Ctx(url =>
            url.StartsWith($"/api/mail/threads/{root.Id}") ? new MailPaged<MailSummary>([Sum(reply), Sum(root)], 2, 1, 50)
            : url == $"/api/mail/{root.Id}" ? root
            : null);

        MailDetail? quick = null, replied = null, repliedAll = null, forwarded = null;
        var cut = ctx.Render<MailThreadStack>(p => p
            .Add(c => c.Detail, reply)
            .Add(c => c.MyNik, Me)
            .Add(c => c.OnReply, d => replied = d)
            .Add(c => c.OnReplyAll, d => repliedAll = d)
            .Add(c => c.OnForward, d => forwarded = d)
            .Add(c => c.OnQuickReply, d => quick = d));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".thr-msg").Count));
        // Open the older root (my own sent message).
        cut.Find($".thr-msg[data-thr-id=\"{root.Id}\"] .thr-head").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($".thr-msg[data-thr-id=\"{root.Id}\"] .thr-acts")));

        foreach (var id in new[] { reply.Id, root.Id })
        {
            var labels = cut.FindAll($".thr-msg[data-thr-id=\"{id}\"] .thr-acts button").Select(b => b.TextContent.Trim()).ToList();
            Assert.Equal(["Quick reply", "Reply", "Reply all", "Forward"], labels);
        }

        Btn(root.Id, "Quick reply").Click(); Assert.Equal(root.Id, quick?.Id);
        Btn(root.Id, "Reply").Click(); Assert.Equal(root.Id, replied?.Id);
        Btn(root.Id, "Reply all").Click(); Assert.Equal(root.Id, repliedAll?.Id);
        Btn(root.Id, "Forward").Click(); Assert.Equal(root.Id, forwarded?.Id);

        AngleSharp.Dom.IElement Btn(Guid id, string label) =>
            cut.FindAll($".thr-msg[data-thr-id=\"{id}\"] .thr-acts button").Single(b => b.TextContent.Trim() == label);
    }

    [Fact]
    public void Reply_all_is_offered_only_when_it_reaches_someone_else()
    {
        var mass = Mail("S1", "Sender", R(Me, "Me", "to")) with { CanReplyAll = false };
        var solo = Mail("S1", "Sender", R(Me, "Me", "to"));
        using var ctx = Ctx(_ => null);
        foreach (var d in new[] { mass, solo })
        {
            var cut = ctx.Render<MailMessageActions>(p => p.Add(c => c.Mail, d).Add(c => c.MyNik, Me));
            Assert.DoesNotContain("Reply all", cut.Markup);
            Assert.Contains("Reply", cut.Markup);
        }
        var group = Mail("S1", "Sender", R(Me, "Me", "to"), R("C1", "Cici", "cc"));
        Assert.Contains("Reply all", ctx.Render<MailMessageActions>(p => p.Add(c => c.Mail, group).Add(c => c.MyNik, Me)).Markup);
    }

    // ---- emoji popup CSS: nothing parks it at the window's top-left ----

    [Fact]
    public void Emoji_popups_have_no_top_left_fallback_and_the_quick_reply_row_does_not_scale()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IPop.slnx"))) root = root.Parent;
        var css = File.ReadAllText(Path.Combine(root!.FullName, "src/IPop.Web/wwwroot/css/components/emoji-picker.css"));
        var compose = System.Text.RegularExpressions.Regex.Match(css, @"\.mrte > \.mrte-emoji-pop \{([^}]*)\}").Groups[1].Value;
        Assert.DoesNotMatch(@"\btop:\s*0\b", compose);
        Assert.DoesNotMatch(@"\bleft:\s*0\b", compose);
        // micro.css scales ".ra:active"; Mail's quick-reply row is a ".ra" holding the emoji button.
        Assert.Matches(@"\.inline-reply-box \.ra:active \{\s*transform:\s*none;", css);
    }
}
