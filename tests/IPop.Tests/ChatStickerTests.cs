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

    [Fact]
    public async Task Sending_a_sticker_puts_it_on_top_of_recent()
    {
        using var t = new TestDb();
        var me = t.AddUser("U1");
        var room = t.AddRoom("group");
        t.AddMember(room.Id, me.Id);
        var chat = t.NewChatService();

        foreach (var key in new[] { "fluent:red-heart", "rocket", "fluent:red-heart" })
            Assert.True((await chat.SendMessageAsync(me.Id, new SendMessageRequest(room.Id, key, Guid.NewGuid(), null, "sticker"), default)).Success);
        await chat.SendMessageAsync(me.Id, new SendMessageRequest(room.Id, "just text", Guid.NewGuid(), null, "text"), default);

        var list = await new StickerService(t.Verify()).ListAsync(me.Id, default);
        Assert.Equal(["fluent:red-heart", "rocket"], list.Recent);
        Assert.Equal(2, t.Verify().StickerUsages.Single(u => u.StickerKey == "fluent:red-heart").UseCount);
        Assert.Empty(list.Favorites);
    }

    [Fact]
    public async Task Recent_is_capped_and_newest_first()
    {
        using var t = new TestDb();
        var me = t.AddUser("U1");
        for (var i = 0; i < StickerLimits.MaxRecent + 5; i++)
            Assert.True(await new StickerService(t.Verify()).RecordUseAsync(me.Id, $"fluent:s{i}", default));

        var recent = (await new StickerService(t.Verify()).ListAsync(me.Id, default)).Recent;
        Assert.Equal(StickerLimits.MaxRecent, recent.Count);
        Assert.Equal($"fluent:s{StickerLimits.MaxRecent + 4}", recent[0]);
        Assert.False(await new StickerService(t.Verify()).RecordUseAsync(me.Id, "<script>", default));
    }

    [Fact]
    public async Task Favorites_cover_custom_and_default_stickers_and_are_per_user()
    {
        using var t = new TestDb();
        var me = t.AddUser("U1");
        var other = t.AddUser("U2");
        var custom = StickerLimits.Content(Guid.NewGuid());

        Assert.True(await new StickerService(t.Verify()).SetFavoriteAsync(me.Id, "fluent:thumbs-up", true, default));
        Assert.True(await new StickerService(t.Verify()).SetFavoriteAsync(me.Id, custom, true, default));
        Assert.True(await new StickerService(t.Verify()).SetFavoriteAsync(me.Id, custom, true, default)); // idempotent
        Assert.False(await new StickerService(t.Verify()).SetFavoriteAsync(me.Id, "custom:nope", true, default));

        var mine = await new StickerService(t.Verify()).ListAsync(me.Id, default);
        Assert.Equal(2, mine.Favorites.Count);
        Assert.Contains("fluent:thumbs-up", mine.Favorites);
        Assert.Empty(mine.Recent); // starring is not sending
        Assert.Empty((await new StickerService(t.Verify()).ListAsync(other.Id, default)).Favorites);

        Assert.True(await new StickerService(t.Verify()).SetFavoriteAsync(me.Id, custom, false, default));
        Assert.Equal(["fluent:thumbs-up"], (await new StickerService(t.Verify()).ListAsync(me.Id, default)).Favorites);
    }

    [Theory]
    [InlineData("custom:0123456789abcdef0123456789abcdef", true)]
    [InlineData("fluent:red-heart", true)]
    [InlineData("rocket", true)]
    [InlineData("check-circle", true)]
    [InlineData("fluent:", false)]
    [InlineData("fluent:Red Heart", false)]
    [InlineData("custom:xyz", false)]
    [InlineData("", false)]
    [InlineData("../etc/passwd", false)]
    public void Sticker_key_validation(string key, bool valid) => Assert.Equal(valid, StickerLimits.IsValidKey(key));

    [Theory]
    [InlineData("a.png", 1000, null)]
    [InlineData("A.WEBP", 1000, null)]
    [InlineData("a.gif", 10 * 1024 * 1024, null)]
    [InlineData("a.jpg", 10 * 1024 * 1024 + 1, "too_large")]
    [InlineData("a.svg", 1000, "not_an_image")]
    [InlineData("a.bmp", 1000, "not_an_image")]
    [InlineData("a.png", 0, "no_file")]
    [InlineData(null, 10, "no_file")]
    public void Upload_validation(string? name, long length, string? expected) =>
        Assert.Equal(expected, StickerEndpoints.Validate(name, length));

    [Fact]
    public void Normalize_fits_into_a_512_transparent_square_without_cropping()
    {
        // A wide red bar: fitted, not cropped, so its full width survives and top/bottom are transparent.
        using var src = new Image<Rgba32>(600, 300, new Rgba32(220, 20, 20, 255));
        var webp = StickerEndpoints.Normalize(Png(src));

        Assert.Equal("WEBP", Image.DetectFormat(webp).Name.ToUpperInvariant());
        using var result = Image.Load<Rgba32>(webp);
        Assert.Equal(512, result.Width);
        Assert.Equal(512, result.Height);
        Assert.True(result[256, 10].A < 16, "padding above the image is transparent");
        Assert.True(result[256, 500].A < 16, "padding below the image is transparent");
        Assert.True(result[4, 256].A > 240, "the left edge of the image is kept");
        Assert.True(result[507, 256].A > 240, "the right edge of the image is kept");
        Assert.True(result[256, 256].R > 180);
    }

    [Fact]
    public void Normalize_keeps_an_editor_made_512_square_as_is()
    {
        using var src = new Image<Rgba32>(512, 512, new Rgba32(0, 0, 0, 0));
        for (var x = 100; x < 412; x++) for (var y = 100; y < 412; y++) src[x, y] = new Rgba32(30, 120, 250, 255);
        using var result = Image.Load<Rgba32>(StickerEndpoints.Normalize(Png(src)));
        Assert.Equal(512, result.Width);
        Assert.True(result[50, 50].A < 16);
        Assert.True(result[105, 105].A > 240, "no re-crop or re-pad shifts the content");
    }

    [Fact]
    public void Normalize_squeezes_a_noisy_photo_under_the_static_target()
    {
        var rnd = new Random(7);
        using var src = new Image<Rgba32>(2000, 2000);
        src.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (ref var p in rows.GetRowSpan(y))
                    p = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255);
        });
        var webp = StickerEndpoints.Normalize(Png(src));
        Assert.True(webp.Length <= StickerLimits.TargetStaticBytes, $"{webp.Length} bytes");
    }

    [Fact]
    public void Normalize_keeps_animation_frames()
    {
        using var gif = new Image<Rgba32>(200, 100, new Rgba32(255, 0, 0, 255));
        using (var f2 = new Image<Rgba32>(200, 100, new Rgba32(0, 0, 255, 255))) gif.Frames.AddFrame(f2.Frames.RootFrame);
        using var ms = new MemoryStream();
        gif.SaveAsGif(ms);
        ms.Position = 0;

        var webp = StickerEndpoints.Normalize(ms);

        using var result = Image.Load(webp);
        Assert.Equal(2, result.Frames.Count);
        Assert.Equal(512, result.Width);
    }

    private static MemoryStream Png(Image image)
    {
        var ms = new MemoryStream();
        image.SaveAsPng(ms);
        ms.Position = 0;
        return ms;
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
        Assert.Equal("Beny Santoso: 🎨 Sticker", RoomPreview.Build("group", "sticker", "rocket", false, "Beny Santoso", false));
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
        Assert.DoesNotContain("sticker-only", builtin.Find(".bubble").ClassName);
    }

    [Fact]
    public void Fluent_sticker_renders_its_self_hosted_image_without_a_bubble()
    {
        using var ctx = Context();
        var cut = ctx.Render<MessageBubble>(p => p.Add(m => m.Message, Msg("fluent:red-heart")));
        var img = cut.Find(".sticker-msg.fluent img.sticker-img");
        Assert.Equal("stickers/fluent/red-heart.webp", img.GetAttribute("src"));
        Assert.Equal("Red heart", img.GetAttribute("alt"));
        Assert.Contains("sticker-only", cut.Find(".bubble").ClassName);

        // An unknown slug never becomes an <img> (nor an arbitrary path).
        var unknown = ctx.Render<MessageBubble>(p => p.Add(m => m.Message, Msg("fluent:not-a-sticker")));
        Assert.Empty(unknown.FindAll("img.sticker-img"));
    }

    [Fact]
    public void Fluent_pack_is_complete_on_disk_and_licensed()
    {
        var dir = Path.Combine(RepoRoot(), "src", "IPop.Web", "wwwroot", "stickers", "fluent");
        Assert.True(FluentStickers.All.Count >= 48);
        Assert.Equal(FluentStickers.All.Count, FluentStickers.All.Select(s => s.Slug).Distinct().Count());
        foreach (var s in FluentStickers.All)
        {
            Assert.True(StickerLimits.IsValidKey(s.Key), s.Key);
            Assert.True(System.IO.File.Exists(Path.Combine(dir, s.Slug + ".webp")), s.Slug);
        }
        Assert.Contains("MIT License", System.IO.File.ReadAllText(Path.Combine(dir, "LICENSE.txt")));
    }

    [Fact]
    public void Fluent_previews_say_sticker()
    {
        Assert.Equal("🎨 Sticker", RoomPreview.Build("dm", "sticker", "fluent:red-heart", false, "Beny", false));
        Assert.Equal("red-heart", StickerLimits.FluentSlug("sticker", "fluent:red-heart"));
        Assert.Null(StickerLimits.FluentSlug("text", "fluent:red-heart"));
        Assert.Null(StickerLimits.FluentSlug("sticker", "fluent:../x"));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(Path.Combine(d.FullName, "IPop.slnx"))) d = d.Parent;
        return d!.FullName;
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
