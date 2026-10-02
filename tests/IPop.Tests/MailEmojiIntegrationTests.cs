using Bunit;
using IPop.Web.Components.Mail;
using Xunit;

namespace IPop.Tests;

public class MailEmojiIntegrationTests
{
    [Fact]
    public void Mail_editor_exposes_labelled_emoji_button_closed_by_default()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = ctx.Render<MailRteEditor>();

        var button = cut.Find("button[aria-label='Emoji']");
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.Contains("Formatting", cut.Markup);
    }
}
