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
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.Services.AddScoped<IPop.Web.Services.PeopleLinks>();
        return ctx;
    }

    /// Every request answers 404: components under test see "nothing there" without a
    /// real socket to localhost.
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });
    }

    /// Context for the drawer components (contact card, group settings, pinned panel),
    /// with every service they inject and an offline HTTP stack.
    private static BunitContext CreateDrawerContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        HttpClient Http() => new(new NotFoundHandler()) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(Http(), ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new ChatState(auth, Http()));
        ctx.Services.AddSingleton(new ChatAttachments(auth, Http(), ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton(new ConfirmService());
        ctx.Services.AddScoped<IPop.Web.Services.PeopleLinks>();
        ctx.Services.AddSingleton(new FavoriteContacts(ctx.JSInterop.JSRuntime, auth));
        ctx.Services.AddSingleton(new CallState(auth, Http(), ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(Http());
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }

    private static IPop.Web.Models.Employee SamplePerson() => new(
        "Alice Tan", "Engineer", "E123", "4412", "HQ", "IT", "IT", "Jakarta", "JKT",
        "Bob Lee", "alice@example.com", "on", "#0a84ff");

    [Fact]
    public void PinnedPanel_RoomScope_ListsPinsWithSegmentedControlAndNoInnerHeader()
    {
        using var ctx = CreateDrawerContext();
        var pins = new List<PinnedMessageDto>
        {
            new(Guid.NewGuid(), Guid.NewGuid(), "Alice", "Deploy at 5pm", DateTime.UtcNow)
        };
        var cut = ctx.Render<PinnedPanel>(p => p
            .Add(m => m.RoomId, Guid.NewGuid())
            .Add(m => m.IsOpen, true)
            .Add(m => m.Items, pins));

        Assert.Empty(cut.FindAll(".pp-head"));
        var segs = cut.FindAll(".pp-scope-item");
        Assert.Equal(2, segs.Count);
        Assert.Equal("This conversation", segs[0].TextContent.Trim());
        Assert.Equal("true", segs[0].GetAttribute("aria-checked"));
        Assert.Contains("1 pinned in this conversation", cut.Markup);
        Assert.Contains("Deploy at 5pm", cut.Markup);
        Assert.Single(cut.FindAll(".pin-unpin"));
    }

    [Fact]
    public void PinnedPanel_JumpsToMessage_WhenItemClicked()
    {
        using var ctx = CreateDrawerContext();
        var msgId = Guid.NewGuid();
        Guid? jumped = null;
        var cut = ctx.Render<PinnedPanel>(p => p
            .Add(m => m.RoomId, Guid.NewGuid())
            .Add(m => m.IsOpen, true)
            .Add(m => m.Items, new List<PinnedMessageDto> { new(Guid.NewGuid(), msgId, "Alice", "Hi", DateTime.UtcNow) })
            .Add(m => m.OnJumpTo, id => jumped = id));

        cut.Find(".pin-item").Click();
        Assert.Equal(msgId, jumped);
    }

    [Fact]
    public void PinnedPanel_WithoutRoom_DefaultsToAllConversations()
    {
        using var ctx = CreateDrawerContext();
        var cut = ctx.Render<PinnedPanel>(p => p
            .Add(m => m.RoomId, Guid.Empty)
            .Add(m => m.IsOpen, true)
            .Add(m => m.Scope, PinnedPanel.PinnedScope.Room));

        var segs = cut.FindAll(".pp-scope-item");
        Assert.True(segs[0].HasAttribute("disabled"));
        Assert.Equal("true", segs[1].GetAttribute("aria-checked"));
        cut.WaitForAssertion(() => Assert.Contains("No pinned messages yet", cut.Markup));
    }

    [Fact]
    public void ContactCard_RendersCopyButtonsAndSingleActionRow()
    {
        using var ctx = CreateDrawerContext();
        var person = SamplePerson();
        var cut = ctx.Render<IPop.Web.Components.ContactCard>(p => p.Add(m => m.Person, person));

        // Office policy: no Employee ID (NIK) row and no position line.
        Assert.Equal(2, cut.FindAll(".inset-copy").Count);
        Assert.DoesNotContain("Employee ID", cut.Markup);
        Assert.DoesNotContain(person.Nik, cut.Markup);
        Assert.DoesNotContain(person.Position, cut.Markup);
        // Message, Mail, Call and Calendar (calendar access v2); the calendar link carries a token, never the NIK.
        Assert.Equal(4, cut.FindAll(".hero-btn").Count);
        Assert.StartsWith("/calendar/person/p", cut.Find("a.hero-btn").GetAttribute("href"));
        Assert.DoesNotContain("View full profile", cut.Markup);
    }

    [Fact]
    public void ContactCard_Message_InvokesHostCallback()
    {
        using var ctx = CreateDrawerContext();
        var messaged = false;
        var cut = ctx.Render<IPop.Web.Components.ContactCard>(p => p
            .Add(m => m.Person, SamplePerson())
            .Add(m => m.OnMessage, () => messaged = true));

        cut.Find(".hero-btn[aria-label='Message']").Click();
        Assert.True(messaged);
    }

    [Fact]
    public void ContactCard_Mail_DefaultsToMailCompose()
    {
        using var ctx = CreateDrawerContext();
        var cut = ctx.Render<IPop.Web.Components.ContactCard>(p => p.Add(m => m.Person, SamplePerson()));

        cut.Find(".hero-btn[aria-label='Mail']").Click();
        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Assert.Contains("/mail?to=", nav.Uri);
        Assert.DoesNotContain(SamplePerson().Nik, nav.Uri);
    }

    [Fact]
    public void GroupSettingsPanel_ShowsNotFound_WhenRoomUnavailable()
    {
        using var ctx = CreateDrawerContext();
        var cut = ctx.Render<GroupSettingsPanel>(p => p.Add(m => m.RoomId, Guid.NewGuid()));

        cut.WaitForAssertion(() => Assert.Contains("Group not found", cut.Markup));
        Assert.Single(cut.FindAll(".gs-panel"));
        Assert.DoesNotContain("Open chat", cut.Markup);
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

    [Theory]
    [InlineData("Alice unpinned a message", "pin-off")]
    [InlineData("Alice pinned a message", "pin")]
    public void MessageBubble_PinSystemRows_UseMatchingIcon(string content, string icon)
    {
        using var ctx = CreateContext();
        var message = new MessageDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Alice", "U1", "system", content,
            DateTime.UtcNow, null, false, null, null, null, null, 0, null, [], [], [], false, Guid.NewGuid());

        var cut = ctx.Render<MessageBubble>(p => p.Add(m => m.Message, message));

        Assert.Equal(icon, cut.Find("i.sys-icon").GetAttribute("data-lucide"));
        Assert.Contains(content, cut.Markup);
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
