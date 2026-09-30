using System.Reflection;
using Bunit;
using IPop.Application.Chat;
using IPop.Web.Components.Chat;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class StickerPickerTests
{
    private static readonly Guid Mine1 = Guid.NewGuid();

    [Fact]
    public void Shows_all_tabs_and_opens_on_default_without_history()
    {
        using var ctx = Context(new([], [], [], []));
        var cut = ctx.Render<StickerPicker>();

        var tabs = cut.FindAll(".sticker-tabs button").Select(b => b.GetAttribute("data-tab")).ToList();
        Assert.Equal(["recent", "favorites", "mine", "default"], tabs); // Official hidden while empty
        Assert.Equal("default", cut.Find("[role=tabpanel]").GetAttribute("data-panel"));
        Assert.Equal(FluentStickers.All.Count, cut.FindAll(".picker-stickers img[src^='stickers/fluent/']").Count);
    }

    [Fact]
    public void Opens_on_recent_when_there_is_history_and_renders_every_key_kind()
    {
        var recent = new List<string> { StickerLimits.Content(Mine1), "fluent:red-heart", "thumbs-up" };
        using var ctx = Context(new([new(Mine1, false, true)], [], recent, ["fluent:red-heart"]));
        var cut = ctx.Render<StickerPicker>();

        Assert.Equal("recent", cut.Find("[role=tabpanel]").GetAttribute("data-panel"));
        Assert.Contains($"api/chat/stickers/{Mine1}/image", cut.Markup);
        Assert.Single(cut.FindAll("img[src='stickers/fluent/red-heart.webp']"));
        Assert.Single(cut.FindAll("i[data-lucide='thumbs-up']"));
        Assert.Single(cut.FindAll(".sticker-fav-dot")); // red heart is a favorite
    }

    [Fact]
    public void Favorites_and_mine_tabs_list_their_stickers_with_a_create_tile()
    {
        using var ctx = Context(new([new(Mine1, false, true)], [new(Guid.NewGuid(), true, false)], [], ["fluent:fire"]));
        var sent = new List<string>();
        var created = 0;
        var cut = ctx.Render<StickerPicker>(p => p
            .Add(x => x.OnSend, k => sent.Add(k))
            .Add(x => x.OnCreate, () => created++));

        Assert.Contains("official", cut.FindAll(".sticker-tabs button").Select(b => b.GetAttribute("data-tab")));

        cut.Find("[data-tab=favorites]").Click();
        Assert.Single(cut.FindAll(".picker-sticker img[src='stickers/fluent/fire.webp']"));
        cut.Find(".picker-sticker.img").Click();
        Assert.Equal(["fluent:fire"], sent);

        cut.Find("[data-tab=mine]").Click();
        Assert.Equal("mine", cut.Find("[role=tabpanel]").GetAttribute("data-panel"));
        cut.Find(".sticker-add").Click();
        Assert.Equal(1, created);
        Assert.Single(cut.FindAll(".picker-sticker.img"));
        Assert.Equal("mine", ctx.Services.GetRequiredService<ChatState>().StickerTab);
    }

    [Fact]
    public void Right_click_opens_the_favorite_action_bar()
    {
        using var ctx = Context(new([], [], [], []));
        var cut = ctx.Render<StickerPicker>();

        cut.Find(".picker-sticker.img").ContextMenu();

        var menu = cut.Find(".sticker-menu");
        Assert.Contains("Add to favorites", menu.TextContent);
        Assert.DoesNotContain("my stickers", menu.TextContent); // fluent stickers aren't collectable
    }

    private static BunitContext Context(StickerListDto stickers)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        var chat = new ChatState(auth, http);
        typeof(ChatState).GetProperty(nameof(ChatState.Stickers))!.SetValue(chat, stickers);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(chat);
        return ctx;
    }
}
