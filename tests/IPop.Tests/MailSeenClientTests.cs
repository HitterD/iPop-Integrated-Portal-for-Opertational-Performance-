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

/// Mail read receipts, client side (spec 2026-10-14 §3): the reading pane marks a message's shown body with
/// data-mail-seen (the JS tracker applies the visible + focused + 2 s rule), and the reporter posts
/// /api/mail/{id}/seen once per message per session. Opening a mail must not post /seen by itself.
public class MailSeenClientTests
{
    private const string Me = "ME01";

    private static WebMail.MailRecipient R(string nik, string type) => new(Guid.NewGuid(), nik, nik, $"{nik}@x.co", type);

    private static MailDetail Mail(string senderNik, string folder = "inbox", params WebMail.MailRecipient[] recipients) => new(
        Guid.NewGuid(), Guid.NewGuid(), senderNik, senderNik, "Budget", "<p>body</p>", true, null,
        DateTime.UtcNow, null, null, 0, false, false, folder, null, null,
        recipients, [], []);

    private static MailSummary Sum(MailDetail d) => new(d.Id, d.SenderId, d.SenderName, d.SenderNik, d.Subject, "snip", d.SentAt,
        true, false, false, d.ThreadCount, d.ThreadRootId is null, d.ThreadRootId);

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

    private static (BunitContext Ctx, FakeApi Api) Ctx(Func<HttpRequestMessage, object?> route)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new FakeApi(route);
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new WebMail.MailService(auth, http));
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddScoped<PhotoViewerState>();
        return (ctx, api);
    }

    // ── which bodies are tracked ──

    [Theory]
    [InlineData("S1", "inbox", true)]
    [InlineData("S1", "archive", true)]
    [InlineData(Me, "sent", false)]       // the sender's own copy is never a receipt
    [InlineData("S1", "draft", false)]
    [InlineData("S1", "scheduled", false)]
    public void Tracks_only_someone_elses_delivered_mail(string sender, string folder, bool expected)
        => Assert.Equal(expected, MailSeen.Applies(Mail(sender, folder), Me));

    [Fact]
    public void Thread_marks_only_open_loaded_bodies_from_others()
    {
        var root = Mail(Me, "sent", R("T1", "to")) with { ThreadCount = 2 };
        var mid = Mail("T1", "inbox", R(Me, "to")) with { ThreadRootId = root.Id, InReplyToId = root.Id };
        var reply = Mail("T2", "inbox", R(Me, "to")) with { ThreadRootId = root.Id, InReplyToId = mid.Id };
        var (ctx, _) = Ctx(req =>
        {
            var url = req.RequestUri!.PathAndQuery;
            return url.StartsWith($"/api/mail/threads/{root.Id}") ? new MailPaged<MailSummary>([Sum(reply), Sum(mid), Sum(root)], 3, 1, 50)
                : url == $"/api/mail/{root.Id}" ? root
                : url == $"/api/mail/{mid.Id}" ? mid
                : null;
        });
        using var _ctx = ctx;
        var cut = ctx.Render<MailThreadStack>(p => p.Add(c => c.Detail, reply).Add(c => c.MyNik, Me));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".thr-msg").Count));

        // Only the opened reply is expanded: one marker, its id.
        Assert.Equal([reply.Id.ToString()], Markers());

        // Expanding someone else's older message adds it once its body has loaded; my own never does.
        cut.Find($".thr-msg[data-thr-id=\"{mid.Id}\"] .thr-head").Click();
        cut.Find($".thr-msg[data-thr-id=\"{root.Id}\"] .thr-head").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { mid.Id.ToString(), reply.Id.ToString() }.Order(), Markers().Order()));

        // Collapsing removes the marker (the JS tracker cancels that message's timer).
        cut.Find($".thr-msg[data-thr-id=\"{mid.Id}\"] .thr-head").Click();
        cut.WaitForAssertion(() => Assert.Equal([reply.Id.ToString()], Markers()));

        string[] Markers() => cut.FindAll("[data-mail-seen]").Select(e => e.GetAttribute("data-mail-seen")!).ToArray();
    }

    // ── reporter ──

    [Fact]
    public async Task Reporter_posts_seen_once_per_message_per_session()
    {
        var id = Guid.NewGuid();
        var (ctx, api) = Ctx(req => req.RequestUri!.PathAndQuery == $"/api/mail/{id}/seen" ? new { recorded = true, seenAt = DateTime.UtcNow } : null);
        using var _ctx = ctx;
        var reporter = new MailSeen(ctx.Services.GetRequiredService<WebMail.MailService>());

        Assert.True(await reporter.ReportAsync(id.ToString()));
        Assert.False(await reporter.ReportAsync(id.ToString()));
        Assert.False(await reporter.ReportAsync("not-a-guid"));
        Assert.Equal([$"POST /api/mail/{id}/seen"], api.Calls);
    }

    [Fact]
    public async Task Reporter_allows_a_retry_after_a_failed_post()
    {
        var id = Guid.NewGuid();
        var fail = true;
        var (ctx, api) = Ctx(_ => fail ? null : new { recorded = true });
        using var _ctx = ctx;
        var reporter = new MailSeen(ctx.Services.GetRequiredService<WebMail.MailService>());

        Assert.False(await reporter.ReportAsync(id.ToString()));
        fail = false;
        Assert.True(await reporter.ReportAsync(id.ToString()));
        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public async Task Receipts_client_decodes_the_api_shape()
    {
        var id = Guid.NewGuid();
        var json = new
        {
            mailId = id, tracked = true, trackedSince = DateTime.UtcNow.AddDays(-1), isSender = false, namesVisible = true,
            total = 2, read = 1, delivered = 2,
            people = new[] { new { userKey = "abc", name = "Rina", department = "ICT", site = "SJA Karawang", photoId = (string?)null, field = "to", viaGroup = false, isMe = true, seenAt = (DateTime?)DateTime.UtcNow, delivered = true } },
            groups = new[] { new { field = "to", kind = "shared", name = "ICT", total = 1, read = 1, memberKeys = new[] { "abc" } } },
            external = new { count = 0, status = "not_available" },
        };
        var (ctx, _) = Ctx(req => req.RequestUri!.PathAndQuery == $"/api/mail/{id}/receipts" ? json : null);
        using var _ctx = ctx;
        var r = await ctx.Services.GetRequiredService<WebMail.MailService>().GetReceiptsAsync(id);
        Assert.NotNull(r);
        Assert.True(r.Tracked);
        Assert.Equal("Rina", Assert.Single(r.People).Name);
        Assert.Equal(["abc"], Assert.Single(r.Groups).MemberKeys);
        Assert.Equal("not_available", r.External.Status);
    }

    // ── the misleading header marker is gone ──

    [Fact]
    public void Reading_pane_header_no_longer_shows_the_viewers_own_read_flag()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IPop.slnx"))) root = root.Parent;
        var page = File.ReadAllText(Path.Combine(root!.FullName, "src/IPop.Web/Pages/Mail.razor"));
        Assert.DoesNotContain("read-marker", page);
        Assert.Contains("data-mail-seen", page);
        Assert.Contains("ipopMailSeen.bind", page);
        var index = File.ReadAllText(Path.Combine(root.FullName, "src/IPop.Web/wwwroot/index.html"));
        Assert.Contains("js/mail-seen.js", index);
    }
}
