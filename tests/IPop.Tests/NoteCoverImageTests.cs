using IPop.Api.Notes;
using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace IPop.Tests;

/// Photo covers: the upload is judged by its CONTENT (not its name), shrunk to 2000 px, and a page may only use an image
/// that is attached to it.
public class NoteCoverImageTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static byte[] Img(int w, int h, string format)
    {
        using var image = new Image<Rgb24>(w, h, new Rgb24(40, 120, 200));
        using var ms = new MemoryStream();
        if (format == "png") image.SaveAsPng(ms);
        else if (format == "gif") image.SaveAsGif(ms);
        else if (format == "bmp") image.SaveAsBmp(ms);
        else if (format == "webp") image.SaveAsWebp(ms);
        else image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    private static NoteCoverImage.Result? Prep(byte[] bytes, out string? err) => NoteCoverImage.Prepare(new MemoryStream(bytes), bytes.Length, out err);

    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    [InlineData("webp")]
    public void Accepts_jpeg_png_and_webp_by_content(string format)
    {
        var r = Prep(Img(300, 200, format), out var err);
        Assert.NotNull(r);
        Assert.Null(err);
        Assert.Equal((300, 200), (r!.Width, r.Height));
        Assert.Equal(format == "png" ? "image/png" : "image/jpeg", r.ContentType);
    }

    [Fact]
    public void Wide_photos_are_scaled_down_to_2000px_keeping_the_ratio()
    {
        var r = Prep(Img(4000, 3000, "jpg"), out _);
        Assert.NotNull(r);
        Assert.Equal(2000, r!.Width);
        Assert.Equal(1500, r.Height);
    }

    [Fact]
    public void Small_photos_are_not_enlarged()
    {
        var r = Prep(Img(800, 400, "jpg"), out _);
        Assert.Equal(800, r!.Width);
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("bmp")]
    public void Other_image_formats_are_refused(string format)
    {
        Assert.Null(Prep(Img(50, 50, format), out var err));
        Assert.Equal("unsupported_format", err);
    }

    [Fact]
    public void A_renamed_non_image_is_refused_whatever_its_extension()
    {
        var notImage = System.Text.Encoding.UTF8.GetBytes("MZ this is an executable pretending to be cover.jpg");
        Assert.Null(Prep(notImage, out var err));
        Assert.Equal("not_an_image", err);
    }

    [Fact]
    public void Empty_and_oversized_files_are_refused_before_decoding()
    {
        Assert.Null(NoteCoverImage.Prepare(new MemoryStream(), 0, out var e1));
        Assert.Equal("empty", e1);
        Assert.Null(NoteCoverImage.Prepare(new MemoryStream([1]), NoteCoverImage.MaxBytes + 1, out var e2));
        Assert.Equal("too_large", e2);
        Assert.NotNull(Prep(Img(10, 10, "jpg"), out _));
    }

    // ── service rules ──────────────────────────────────────────────────────────

    private static NoteService Svc(TestDb t) => new(t.Verify());

    private static async Task<Guid> PageAsync(TestDb t, Guid owner)
        => (await Svc(t).CreateAsync(owner, new CreateNoteRequest(null, "P", null, null, null, null), Ct)).Data!.Id;

    private static async Task<Guid> AttachAsync(TestDb t, Guid page, Guid uploader, string contentType)
    {
        var id = Guid.NewGuid();
        t.Db.Attachments.Add(new IPop.Core.Entities.Attachment
        {
            Id = id, Area = "notes", FileName = "f", ContentType = contentType, Size = 5, Sha256 = "x",
            StoragePath = "local:x", UploaderId = uploader, CreatedAt = DateTime.UtcNow,
        });
        await t.Db.SaveChangesAsync();
        await Svc(t).LinkAttachmentAsync(page, id, Ct);
        return id;
    }

    [Fact]
    public async Task Only_an_image_attached_to_the_page_can_become_its_cover()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var other = await PageAsync(t, a.Id);
        var img = await AttachAsync(t, page, a.Id, "image/jpeg");
        var pdf = await AttachAsync(t, page, a.Id, "application/pdf");
        var elsewhere = await AttachAsync(t, other, a.Id, "image/jpeg");

        Assert.False((await Svc(t).SetCoverImageAsync(a.Id, page, pdf, Ct)).Success);
        Assert.False((await Svc(t).SetCoverImageAsync(a.Id, page, elsewhere, Ct)).Success);
        var ok = await Svc(t).SetCoverImageAsync(a.Id, page, img, Ct);
        Assert.True(ok.Success, ok.Error);
        Assert.Equal(img, ok.Data!.CoverImageId);
        Assert.Equal(50, ok.Data.CoverPosY);
    }

    [Fact]
    public async Task Choosing_a_gradient_or_none_replaces_the_photo_and_position_is_clamped()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var img = await AttachAsync(t, page, a.Id, "image/png");
        await Svc(t).SetCoverImageAsync(a.Id, page, img, Ct);

        var moved = await Svc(t).UpdateMetaAsync(a.Id, page, new UpdateNoteMetaRequest(null, null, null, null, null, null, null, null, 250), Ct);
        Assert.Equal(100, moved.Data!.CoverPosY);
        var low = await Svc(t).UpdateMetaAsync(a.Id, page, new UpdateNoteMetaRequest(null, null, null, null, null, null, null, null, -5), Ct);
        Assert.Equal(0, low.Data!.CoverPosY);

        var grad = await Svc(t).UpdateMetaAsync(a.Id, page, new UpdateNoteMetaRequest(null, null, "ocean", null, null, null, null), Ct);
        Assert.Equal("ocean", grad.Data!.Cover);
        Assert.Null(grad.Data.CoverImageId);
        Assert.Equal(50, grad.Data.CoverPosY);

        await Svc(t).SetCoverImageAsync(a.Id, page, img, Ct);
        var none = await Svc(t).UpdateMetaAsync(a.Id, page, new UpdateNoteMetaRequest(null, null, "", null, null, null, null), Ct);
        Assert.Null(none.Data!.Cover);
        Assert.Null(none.Data.CoverImageId);
    }

    [Fact]
    public async Task Viewers_cannot_change_the_cover_but_can_list_the_pages_images()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var page = await PageAsync(t, a.Id);
        var img = await AttachAsync(t, page, a.Id, "image/jpeg");
        await AttachAsync(t, page, a.Id, "application/pdf");
        await Svc(t).AddShareAsync(a.Id, page, new AddNoteShareRequest(b.Id, null, NoteLevels.View), Ct);

        Assert.False((await Svc(t).SetCoverImageAsync(b.Id, page, img, Ct)).Success);
        var list = await Svc(t).GetPageImagesAsync(b.Id, page, Ct);
        Assert.Equal(new[] { img }, list.Data!.Select(i => i.Id).ToArray());
        var stranger = t.AddUser("c");
        Assert.Equal("not_found", (await Svc(t).GetPageImagesAsync(stranger.Id, page, Ct)).Error);
    }

    [Fact]
    public async Task Duplicate_points_the_copys_cover_at_its_own_cloned_image()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var img = await AttachAsync(t, page, a.Id, "image/jpeg");
        await Svc(t).SetCoverImageAsync(a.Id, page, img, Ct);
        await Svc(t).UpdateMetaAsync(a.Id, page, new UpdateNoteMetaRequest(null, null, null, null, null, null, null, null, 20), Ct);

        var dup = await Svc(t).DuplicateAsync(a.Id, page, Ct);
        Assert.True(dup.Success, dup.Error);
        Assert.NotNull(dup.Data!.CoverImageId);
        Assert.NotEqual(img, dup.Data.CoverImageId);
        Assert.Equal(20, dup.Data.CoverPosY);
        Assert.True(await t.Verify().NoteAttachments.AnyAsync(x => x.PageId == dup.Data.Id && x.AttachmentId == dup.Data.CoverImageId));
    }
}
