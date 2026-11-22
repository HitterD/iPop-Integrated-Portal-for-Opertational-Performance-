using System.Net;
using Bunit;
using IPop.Application.Chat;
using IPop.Web.Components;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Notes file cards hand the shared viewer a server-made PDF for PowerPoint and old Word files; Chat and Projects (no
/// ConvertedPdf) keep the "No in-browser preview" card.
public class AttachmentViewerConvertedPdfTests
{
    private sealed class NotFoundApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static BunitContext NewContext(out ChatAttachments att)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new NotFoundApi()) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new MediaStream(auth, http));
        att = new ChatAttachments(auth, http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(att);
        return ctx;
    }

    private static readonly AttachmentDto Deck = new(Guid.NewGuid(), "Deck.pptx", 1000, "", null);

    [Fact]
    public void Without_a_converted_pdf_a_pptx_shows_no_in_browser_preview()
    {
        using var ctx = NewContext(out var att);
        att.UseSharedStore([Deck.Id]);
        ctx.JSInterop.Setup<object?>("ipop.authBlob2", _ => true);
        var cut = ctx.Render<AttachmentViewer>(p => p.Add(x => x.Attachment, Deck));
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "ipop.authBlob2"));
        // unchanged behaviour: the .pptx itself is what loads, never drawn as a PDF frame
        Assert.Equal($"http://localhost/api/attachments/{Deck.Id}", ctx.JSInterop.Invocations.First(i => i.Identifier == "ipop.authBlob2").Arguments[0]);
        Assert.DoesNotContain("av-pdf", cut.Markup);
    }

    [Fact]
    public void With_a_converted_pdf_a_pptx_is_shown_as_that_pdf()
    {
        using var ctx = NewContext(out var att);
        var pdfId = Guid.NewGuid();
        att.UseSharedStore([Deck.Id, pdfId]);
        var blob = ctx.JSInterop.Setup<object?>("ipop.authBlob2", _ => true);
        var cut = ctx.Render<AttachmentViewer>(p => p
            .Add(x => x.Attachment, Deck)
            .Add(x => x.ConvertedPdf, a => a.Id == Deck.Id ? pdfId : null));

        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "ipop.authBlob2"));
        var fetched = ctx.JSInterop.Invocations.First(i => i.Identifier == "ipop.authBlob2").Arguments[0] as string;
        Assert.Equal($"http://localhost/api/attachments/{pdfId}", fetched);   // the PDF's bytes, not the .pptx
        Assert.DoesNotContain("No in-browser preview", cut.Markup);
    }

    [Fact]
    public void A_converted_pdf_is_ignored_for_types_the_browser_already_draws()
    {
        using var ctx = NewContext(out var att);
        var png = new AttachmentDto(Guid.NewGuid(), "pic.png", 10, "image/png", null);
        var other = Guid.NewGuid();
        att.UseSharedStore([png.Id, other]);
        ctx.JSInterop.Setup<object?>("ipop.authBlob2", _ => true);
        var cut = ctx.Render<AttachmentViewer>(p => p.Add(x => x.Attachment, png).Add(x => x.ConvertedPdf, _ => other));
        cut.WaitForAssertion(() => Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "ipop.authBlob2"));
        Assert.Equal($"http://localhost/api/attachments/{png.Id}", ctx.JSInterop.Invocations.First(i => i.Identifier == "ipop.authBlob2").Arguments[0]);
    }
}
