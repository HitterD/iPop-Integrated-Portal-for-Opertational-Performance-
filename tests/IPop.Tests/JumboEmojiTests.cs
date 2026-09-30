using Bunit;
using IPop.Application.Chat;
using IPop.Web.Components.Chat;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class JumboEmojiTests
{
    [Theory]
    [InlineData("😂", 1)]
    [InlineData(" 👍 ", 1)]
    [InlineData("❤️", 1)]
    [InlineData("👍🏽", 1)]                 // skin tone modifier
    [InlineData("👨‍👩‍👧", 1)]             // ZWJ family
    [InlineData("🇮🇩", 1)]                  // flag
    [InlineData("1️⃣", 1)]                  // keycap
    [InlineData("😂😂", 2)]
    [InlineData("🔥 🎉 ✅", 3)]
    [InlineData("😂😂😂😂", 0)]            // more than 3 → normal text
    [InlineData("haha 😂", 0)]
    [InlineData("ok", 0)]
    [InlineData("123", 0)]
    [InlineData(":)", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Counts_only_pure_emoji_messages(string? text, int expected) =>
        Assert.Equal(expected, ChatFormatters.JumboEmojiCount(text));

    [Fact]
    public void Emoji_only_message_renders_without_a_bubble_and_sized_by_count()
    {
        using var ctx = Context();
        var cut = ctx.Render<MessageBubble>(p => p.Add(m => m.Message, Msg("🎉🎉")));
        var bubble = cut.Find(".bubble");
        Assert.Contains("jumbo-emoji", bubble.ClassName);
        Assert.Contains("jumbo-2", bubble.ClassName);
    }

    [Fact]
    public void Text_with_emoji_keeps_the_normal_bubble()
    {
        using var ctx = Context();
        var cut = ctx.Render<MessageBubble>(p => p.Add(m => m.Message, Msg("mantap 🎉")));
        Assert.DoesNotContain("jumbo-emoji", cut.Find(".bubble").ClassName);
    }

    private static MessageDto Msg(string content) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Alice", "U1", "text", content,
        DateTime.UtcNow, null, false, null, null, null, null, 0, null, [], [], [], false, Guid.NewGuid());

    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        var auth = new AuthState(new HttpClient { BaseAddress = new Uri("http://localhost/") }, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new ChatState(auth, new HttpClient { BaseAddress = new Uri("http://localhost/") }));
        ctx.Services.AddSingleton(new ChatAttachments(auth, new HttpClient { BaseAddress = new Uri("http://localhost/") }, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(new ToastService());
        return ctx;
    }
}
