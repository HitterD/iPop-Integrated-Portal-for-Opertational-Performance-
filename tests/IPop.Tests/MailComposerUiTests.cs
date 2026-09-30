using Bunit;
using IPop.Web.Components.Mail;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class MailComposerUiTests
{
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

        var atts = new List<MailAttachment>
        {
            new(Guid.NewGuid(), "report.pdf", 2048, "application/pdf", null),
            new(Guid.NewGuid(), "sheet.xlsx", 4096, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null),
        };
        var cut = ctx.Render<MailAttachmentCards>(p => p.Add(c => c.Attachments, atts));

        Assert.Equal(2, cut.FindAll(".att-card").Count);
        Assert.Contains("2 attachments", cut.Find(".att-head").TextContent);
        Assert.Single(cut.FindAll(".att-card.previewable")); // pdf previews, xlsx downloads
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

        var cut = ctx.Render<MailAttachmentCards>(p => p.Add(c => c.Attachments, new List<MailAttachment>()));
        Assert.Empty(cut.FindAll(".detail-att-section"));
    }
}
