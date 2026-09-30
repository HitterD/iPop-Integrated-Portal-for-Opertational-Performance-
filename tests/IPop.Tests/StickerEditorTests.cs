using Bunit;
using IPop.Web.Components.Chat;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class StickerEditorTests
{
    [Fact]
    public void Closed_by_default_and_opens_empty_with_a_file_prompt()
    {
        using var ctx = Context();
        var cut = ctx.Render<StickerEditor>();
        Assert.Empty(cut.FindAll(".sticker-editor"));

        cut.InvokeAsync(() => cut.Instance.Open());

        Assert.Single(cut.FindAll(".sticker-editor[role=dialog]"));
        Assert.Contains("Choose image", cut.Find(".se-drop").TextContent);
        Assert.Equal(2, cut.FindAll(".se-foot button[disabled]").Count); // nothing to save yet
    }

    [Fact]
    public void Animated_source_skips_the_editor_with_a_notice()
    {
        using var ctx = Context();
        var cut = ctx.Render<StickerEditor>();
        cut.InvokeAsync(() => cut.Instance.Open());

        cut.InvokeAsync(() => cut.Instance.OnSourceLoaded("animated"));

        Assert.Contains("Animated stickers are uploaded as-is", cut.Markup);
        Assert.Empty(cut.FindAll(".se-toolbar"));
        Assert.Empty(cut.FindAll(".se-foot button[disabled]"));
    }

    [Fact]
    public void Background_toggles_are_disabled_when_no_person_is_found()
    {
        using var ctx = Context();
        var cut = ctx.Render<StickerEditor>();
        cut.InvokeAsync(() => cut.Instance.Open());
        cut.InvokeAsync(() => cut.Instance.OnSourceLoaded("image"));

        cut.InvokeAsync(() => cut.Instance.OnEditorState(new() { HasImage = true, Seg = "none" }));
        Assert.Contains("No person found", cut.Find(".se-status").TextContent);
        Assert.All(cut.FindAll(".se-switch input"), i => Assert.True(i.HasAttribute("disabled")));

        cut.InvokeAsync(() => cut.Instance.OnEditorState(new() { HasImage = true, Seg = "person", RemoveBg = true, Outline = true }));
        Assert.All(cut.FindAll(".se-switch input"), i => Assert.False(i.HasAttribute("disabled")));
        Assert.Contains("background removed", cut.Find(".se-status").TextContent);
    }

    [Fact]
    public void Selecting_a_text_overlay_loads_it_into_the_caption_box()
    {
        using var ctx = Context();
        var cut = ctx.Render<StickerEditor>();
        cut.InvokeAsync(() => cut.Instance.Open());
        cut.InvokeAsync(() => cut.Instance.OnSourceLoaded("image"));

        cut.InvokeAsync(() => cut.Instance.OnEditorState(new()
        {
            HasImage = true, Seg = "none",
            Selected = new() { Kind = "text", Text = "Mantap", Color = "#FF3B30", Stroke = true },
        }));

        Assert.Equal("Mantap", cut.Find(".se-text").GetAttribute("value"));
        Assert.Contains("Update text", cut.Markup);
        Assert.Contains("Delete", cut.Find(".se-selected").TextContent);
    }

    [Fact]
    public async Task Escape_closes_it_through_the_overlay_stack()
    {
        using var ctx = Context();
        var stack = ctx.Services.GetRequiredService<OverlayStack>();
        var cut = ctx.Render<StickerEditor>();
        await cut.InvokeAsync(() => cut.Instance.Open());
        Assert.True(stack.Any);

        await cut.InvokeAsync(stack.CloseTopAsync);

        Assert.Empty(cut.FindAll(".sticker-editor"));
        Assert.False(stack.Any);
    }

    [Theory]
    [InlineData("too_large", "max 10 MB")]
    [InlineData("limit_reached", "max 200")]
    [InlineData("not_an_image", "PNG, JPG, GIF or WebP")]
    public void Error_texts_are_english(string code, string expected) =>
        Assert.Contains(expected, StickerEditor.ErrorText(code));

    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new ChatState(auth, http));
        ctx.Services.AddSingleton(new ChatAttachments(auth, http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(new OverlayStack());
        return ctx;
    }
}
