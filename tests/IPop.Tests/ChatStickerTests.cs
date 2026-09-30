using Bunit;
using IPop.Api.Endpoints;
using IPop.Application.Chat;
using IPop.Infrastructure.Chat;
using IPop.Infrastructure.Notifications.Push;
using IPop.Web.Components.Chat;
using IPop.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace IPop.Tests;

public class ChatStickerTests
{
    private static readonly StickerFile File1 = new("local", "chat-files/stickers/a.webp");

    [Fact]
    public async Task Create_puts_the_sticker_in_my_collection_only()
    {
        using var t = new TestDb();
        var me = t.AddUser("U1");
        var other = t.AddUser("U2");

        var r = await new StickerService(t.Verify()).CreateAsync(me.Id, File1, 1234, default);

        Assert.True(r.Success);
        var mine = await new StickerService(t.Verify()).ListAsync(me.Id, default);
        Assert.Equal(r.Sticker!.Id, Assert.Single(mine.Mine).Id);
        Assert.Empty(mine.Official);
        Assert.Empty((await new StickerService(t.Verify()).ListAsync(other.Id, default)).Mine);
    }

    [Fact]
    public async Task Save_is_idempotent_and_remove_keeps_the_sticker_itself()
    {
        using var t = new TestDb();
        var me = t.AddUser("U1");
        var other = t.AddUser("U2");
        var id = (await new StickerService(t.Verify()).CreateAsync(other.Id, File1, 10, default)).Sticker!.Id;

        Assert.True((await new StickerService(t.Verify()).SaveAsync(me.Id, id, default)).Success);
        Assert.True((await new StickerService(t.Verify()).SaveAsync(me.Id, id, default)).Success);
        Assert.Single((await new StickerService(t.Verify()).ListAsync(me.Id, default)).Mine);
        Assert.Equal("not_found", (await new StickerService(t.Verify()).SaveAsync(me.Id, Guid.NewGuid(), default)).Error);

        Assert.True(await new StickerService(t.Verify()).RemoveAsync(me.Id, id, default));
        Assert.Empty((await new StickerService(t.Verify()).ListAsync(me.Id, default)).Mine);
        // Sent messages still render it, and the creator keeps it.
        Assert.NotNull(await new StickerService(t.Verify()).GetFileAsync(id, default));
        Assert.Single((await new StickerService(t.Verify()).ListAsync(other.Id, default)).Mine);
    }

    [Fact]
    public async Task Official_stickers_show_for_everyone_and_flag_the_ones_I_have()
    {
        using var t = new TestDb();
        var maker = t.AddUser("U1");
        var viewer = t.AddUser("U2");
        var id = (await new StickerService(t.Verify()).CreateAsync(maker.Id, File1, 10, default)).Sticker!.Id;

        Assert.True(await new StickerService(t.Verify()).SetOfficialAsync(id, true, default));

        var forViewer = await new StickerService(t.Verify()).ListAsync(viewer.Id, default);
        var official = Assert.Single(forViewer.Official);
        Assert.False(official.IsMine);
        Assert.True(Assert.Single((await new StickerService(t.Verify()).ListAsync(maker.Id, default)).Official).IsMine);

        Assert.True(await new StickerService(t.Verify()).SetOfficialAsync(id, false, default));
        Assert.Empty((await new StickerService(t.Verify()).ListAsync(viewer.Id, default)).Official);
        Assert.False(await new StickerService(t.Verify()).SetOfficialAsync(Guid.NewGuid(), true, default));
    }

    [Fact]
    public async Task Collection_is_capped()
    {
        using var t = new TestDb();
        var me = t.AddUser("U1");
        for (var i = 0; i < StickerLimits.MaxPerUser; i++)
        {
            t.Db.ChatStickers.Add(new() { Id = Guid.NewGuid(), CreatedById = me.Id, StorageServer = "local", StoragePath = $"p{i}", CreatedAt = DateTime.UtcNow });
        }
        t.Db.SaveChanges();
        foreach (var s in t.Db.ChatStickers.ToList())
            t.Db.UserStickers.Add(new() { UserId = me.Id, StickerId = s.Id, AddedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        Assert.Equal("limit_reached", (await new StickerService(t.Verify()).CreateAsync(me.Id, File1, 10, default)).Error);
        Assert.Equal(StickerLimits.MaxPerUser, await t.Verify().ChatStickers.CountAsync());
    }

    [Theory]
    [InlineData("a.png", 1000, null)]
    [InlineData("A.WEBP", 1000, null)]
    [InlineData("a.gif", 512 * 1024, null)]
    [InlineData("a.gif", 512 * 1024 + 1, "too_large")]
    [InlineData("a.svg", 1000, "not_an_image")]
    [InlineData("a.bmp", 1000, "not_an_image")]
    [InlineData("a.png", 0, "no_file")]
    [InlineData(null, 10, "no_file")]
    public void Upload_validation(string? name, long length, string? expected) =>
        Assert.Equal(expected, StickerEndpoints.Validate(name, length));

    [Fact]
    public void Normalize_center_crops_to_a_256_square_webp()
    {
        using var src = new Image<Rgba32>(600, 300);
        using var ms = new MemoryStream();
        src.SaveAsPng(ms);
        ms.Position = 0;

        var webp = StickerEndpoints.Normalize(ms);

        using var result = Image.Load(webp);
        Assert.Equal(256, result.Width);
        Assert.Equal(256, result.Height);
        Assert.Equal("WEBP", Image.DetectFormat(webp).Name.ToUpperInvariant());
    }

    [Theory]
    [InlineData("sticker", "custom:0123456789abcdef0123456789abcdef", true)]
    [InlineData("sticker", "rocket", false)]
    [InlineData("text", "custom:0123456789abcdef0123456789abcdef", false)]
    [InlineData("sticker", "custom:nope", false)]
    public void CustomId_only_for_custom_sticker_messages(string type, string content, bool isCustom) =>
        Assert.Equal(isCustom, StickerLimits.CustomId(type, content) is not null);

    [Fact]
    public void Previews_never_show_the_raw_sticker_content()
    {
        var content = StickerLimits.Content(Guid.NewGuid());
        Assert.Equal("🎨 Sticker", RoomPreview.Build("dm", "sticker", content, false, "Beny", false));
        Assert.Equal("Beny: 🎨 Sticker", RoomPreview.Build("group", "sticker", "rocket", false, "Beny Santoso", false));
    }

    [Fact]
    public void Custom_sticker_renders_as_an_image_and_builtin_as_an_icon()
    {
        using var ctx = Context();
        var id = Guid.NewGuid();
        var custom = ctx.Render<MessageBubble>(p => p.Add(m => m.Message, Msg(StickerLimits.Content(id))));
        Assert.Equal($"http://localhost/api/chat/stickers/{id}/image", custom.Find(".sticker-msg img.sticker-img").GetAttribute("src"));

        var builtin = ctx.Render<MessageBubble>(p => p.Add(m => m.Message, Msg("rocket")));
        Assert.Equal("rocket", builtin.Find(".sticker-big i").GetAttribute("data-lucide"));
        Assert.Empty(builtin.FindAll("img.sticker-img"));
    }

    private static MessageDto Msg(string content) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Alice", "U1", "sticker", content,
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
