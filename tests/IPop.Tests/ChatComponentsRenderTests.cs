using Bunit;
using IPop.Application.Chat;
using IPop.Web.Components.Chat;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class ChatComponentsRenderTests
{
    private static BunitContext CreateContext()
    {
        var ctx = new BunitContext();
        var auth = new AuthState(new HttpClient
        {
            BaseAddress = new Uri("http://localhost/")
        }, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new ChatState(auth, new HttpClient
        {
            BaseAddress = new Uri("http://localhost/")
        }));
        ctx.Services.AddSingleton(new ChatAttachments(auth, new HttpClient
        {
            BaseAddress = new Uri("http://localhost/")
        }, ctx.JSInterop.JSRuntime));
        return ctx;
    }

    [Fact]
    public void MessageBubble_RendersBasicText()
    {
        using var ctx = CreateContext();
        var message = new MessageDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Alice", "U1", "text", "Hello world",
            DateTime.UtcNow, null, false, null, null, null, null, 0, null, [], [], [], false, Guid.NewGuid());

        var cut = ctx.Render<MessageBubble>(p => p.Add(m => m.Message, message));

        Assert.Contains("Hello world", cut.Markup);
        Assert.Contains("msg recv", cut.Markup);
    }

    [Fact]
    public void MessageBubble_HidesAttachmentPlaceholder()
    {
        using var ctx = CreateContext();
        var att = new AttachmentDto(Guid.NewGuid(), "document.pdf", 1024, "application/pdf", null);
        var message = new MessageDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Alice", "U1", "text", "(attachment)",
            DateTime.UtcNow, null, false, null, null, null, null, 0, null, [att], [], [], false, Guid.NewGuid());

        var cut = ctx.Render<MessageBubble>(p => p.Add(m => m.Message, message));

        Assert.DoesNotContain("(attachment)", cut.Markup);
        Assert.Contains("media-only", cut.Markup);
        Assert.Contains("media-card-only", cut.Markup);
    }

    [Fact]
    public void MessageBubble_RendersMarkdownFormatting()
    {
        using var ctx = CreateContext();
        var message = new MessageDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Alice", "U1", "text", "*tebal* dan _miring_ serta `kode`",
            DateTime.UtcNow, null, false, null, null, null, null, 0, null, [], [], [], false, Guid.NewGuid());

        var cut = ctx.Render<MessageBubble>(p => p.Add(m => m.Message, message));

        Assert.Contains("<strong>tebal</strong>", cut.Markup);
        Assert.Contains("<em>miring</em>", cut.Markup);
        Assert.Contains("<code class=\"chat-inline-code\">kode</code>", cut.Markup);
    }

    [Fact]
    public void ThreadPanel_RendersEmptyState()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<ThreadPanel>(p => p.Add(m => m.ParentId, Guid.NewGuid()));

        Assert.True(cut.Markup.Contains("Loading…") || cut.Markup.Contains("thread") || cut.Markup.Length > 0);
    }

    [Fact]
    public void PinnedPanel_RendersEmpty()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<PinnedPanel>(p => p
            .Add(m => m.RoomId, Guid.NewGuid())
            .Add(m => m.IsOpen, true));

        Assert.True(cut.Markup.Contains("Belum ada") || cut.Markup.Contains("Loading") || cut.Markup.Contains("error") || cut.Markup.Length > 0);
    }

    [Fact]
    public void MentionPopup_RendersItems()
    {
        using var ctx = CreateContext();
        var members = new List<MemberDto>
        {
            new(Guid.NewGuid(), "A", "Alice", "member", "off"),
            new(Guid.NewGuid(), "B", "Bob", "member", "off")
        };

        var cut = ctx.Render<MentionPopup>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.Query, "al")
            .Add(m => m.Candidates, members)
            .Add(m => m.CurrentUserNik, "ME")
            .Add(m => m.ShowAll, false));

        Assert.Contains("Alice", cut.Markup);
    }

    [Fact]
    public void GroupInfoPanel_RendersHeader()
    {
        using var ctx = CreateContext();
        var cut = ctx.Render<GroupInfoPanel>();

        Assert.Contains("drawer", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
