using Bunit;
using IPop.Web.Components.Mail;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class MailComposerUiTests
{
    private sealed class RsvpHttp(string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void Rsvp_ShowsConflictAndExcludesCurrentMeetingFromSelfFreeBusy()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = new RsvpHttp("""{"lanes":[{"nik":"ME","name":"Me","blocks":[{"startAt":"2026-10-12T09:15:00Z","endAt":"2026-10-12T10:15:00Z","tentative":false}]}],"suggestions":[]}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(new AuthState(http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(new ToastService());
        var id = Guid.NewGuid();
        var cut = ctx.Render<MeetingRsvpPanel>(p => p.Add(c => c.Meeting,
            new(id, "Review", new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc), null, null)));
        cut.WaitForAssertion(() => Assert.Contains("conflicts with another event", cut.Markup));
        Assert.Contains(handler.Requests, u => u.StartsWith("/api/calendar/freebusy?") && u.Contains($"meeting={id}") && !u.Contains("niks="));
        Assert.False(cut.Find(".rsvp-btn.accept").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData("{\"lanes\":[{\"nik\":\"ME\",\"blocks\":[]}]}" , "No conflicts on your calendar.")]
    [InlineData("{\"lanes\":[{\"nik\":\"ME\",\"blocks\":[{\"startAt\":\"2026-10-12T10:00:00Z\",\"endAt\":\"2026-10-12T11:00:00Z\"}]}]}", "No conflicts on your calendar.")]
    [InlineData("not json", "Couldn't check your calendar. You can still respond.")]
    public void Rsvp_ClearAndUnavailableChecksRemainAdvisory(string body, string expected)
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new RsvpHttp(body)) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(new AuthState(http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(new ToastService());
        var cut = ctx.Render<MeetingRsvpPanel>(p => p.Add(c => c.Meeting,
            new(Guid.NewGuid(), "Review", new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc), null, null)));
        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Markup));
        Assert.False(cut.Find(".rsvp-btn.accept").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("  https://intranet/x  ", "https://intranet/x")]
    [InlineData("budi@corp.id", "mailto:budi@corp.id")]
    [InlineData("mailto:budi@corp.id", "mailto:budi@corp.id")]
    [InlineData("tel:+62811", "tel:+62811")]
    public void NormalizeUrl_AddsSchemeWhereMissing(string raw, string expected)
        => Assert.Equal(expected, MailRteEditor.NormalizeUrl(raw));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("vbscript:msgbox")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizeUrl_RefusesScriptSchemesAndBlank(string? raw)
        => Assert.Null(MailRteEditor.NormalizeUrl(raw));

    [Theory]
    [InlineData("photo.JPG", "image/jpeg", "image")]
    [InlineData("scan.pdf", "application/pdf", "pdf")]
    [InlineData("budget.xlsx", "application/octet-stream", "sheet")]
    [InlineData("memo.docx", "", "doc")]
    [InlineData("deck.pptx", "", "slides")]
    [InlineData("bundle.zip", "application/zip", "archive")]
    [InlineData("vector.svg", "image/svg+xml", "file")]
    public void AttachmentKind_FollowsMimeThenExtension(string name, string mime, string expected)
        => Assert.Equal(expected, MailAttachmentCards.KindOf(new MailAttachment(Guid.NewGuid(), name, 10, mime, null)));

    [Fact]
    public void AttachmentCards_RenderOneCardPerFile_WithoutUnauthenticatedLinks()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(new AuthState(http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(sp => new IPop.Web.Services.MediaStream(sp.GetRequiredService<AuthState>(), new HttpClient { BaseAddress = new Uri("http://localhost/") }));

        var atts = new List<MailAttachment>
        {
            new(Guid.NewGuid(), "report.pdf", 2048, "application/pdf", null),
            new(Guid.NewGuid(), "sheet.xlsx", 4096, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null),
            new(Guid.NewGuid(), "demo.mp4", 40_000_000, "video/mp4", null),
        };
        var cut = ctx.Render<MailAttachmentCards>(p => p.Add(c => c.Attachments, atts));

        Assert.Equal(3, cut.FindAll(".att-card").Count);
        Assert.Contains("3 attachments", cut.Find(".att-head").TextContent);
        // pdf and video preview (video streams, no download first); xlsx downloads. Every card keeps Download.
        Assert.Equal(2, cut.FindAll(".att-card.previewable").Count);
        Assert.Equal(3, cut.FindAll(".att-dl").Count);
        Assert.DoesNotContain("/api/mail/attachments", cut.Markup); // everything goes through authBlob2/authDownload
        Assert.DoesNotContain(ctx.JSInterop.Invocations, i => i.Identifier == "window.open");
    }

    [Fact]
    public void AttachmentCards_RenderNothing_WhenNoAttachments()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(new AuthState(http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(sp => new IPop.Web.Services.MediaStream(sp.GetRequiredService<AuthState>(), new HttpClient { BaseAddress = new Uri("http://localhost/") }));

        var cut = ctx.Render<MailAttachmentCards>(p => p.Add(c => c.Attachments, new List<MailAttachment>()));
        Assert.Empty(cut.FindAll(".detail-att-section"));
    }
}
