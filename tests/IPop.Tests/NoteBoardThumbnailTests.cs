using IPop.Api.Notes;
using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace IPop.Tests;

/// The Whiteboards list shows a small picture of each board. The picture is judged by content, kept small, belongs to a board only,
/// and a new one replaces (never adds to) the old one.
public class NoteBoardThumbnailTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Svc(TestDb t) => new(t.Verify());

    private static byte[] Png(int w, int h)
    {
        using var image = new Image<Rgb24>(w, h, new Rgb24(40, 120, 200));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public void A_big_board_picture_is_shrunk_to_640_wide_and_stored_as_jpeg()
    {
        var bytes = Png(1920, 1080);
        var r = NoteBoardThumbnail.Prepare(new MemoryStream(bytes), bytes.Length, out var err);
        Assert.Null(err);
        Assert.Equal("image/jpeg", r!.ContentType);
        using var shrunk = Image.Load(r.Bytes);
        Assert.Equal(640, shrunk.Width);
        Assert.Equal(360, shrunk.Height);
    }

    [Theory]
    [InlineData("not a picture")]
    [InlineData("")]
    public void Something_that_is_not_a_picture_is_refused(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        Assert.Null(NoteBoardThumbnail.Prepare(new MemoryStream(bytes), bytes.Length, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Too_large_an_upload_is_refused_before_it_is_decoded()
    {
        Assert.Null(NoteBoardThumbnail.Prepare(new MemoryStream([1]), NoteBoardThumbnail.MaxBytes + 1, out var err));
        Assert.Equal("too_large", err);
    }

    private static async Task<Guid> PageAsync(TestDb t, Guid owner, string kind)
        => (await Svc(t).CreateAsync(owner, new CreateNoteRequest(null, "B", null, null, null, null, kind), Ct)).Data!.Id;

    private static async Task<Guid> AttachAsync(TestDb t, Guid page, Guid uploader, string contentType = "image/jpeg")
    {
        var id = Guid.NewGuid();
        t.Db.Attachments.Add(new IPop.Core.Entities.Attachment
        {
            Id = id, Area = "notes", FileName = "board-thumbnail.jpg", ContentType = contentType, Size = 5, Sha256 = "x",
            StoragePath = "local:x", UploaderId = uploader, CreatedAt = DateTime.UtcNow,
        });
        await t.Db.SaveChangesAsync();
        await Svc(t).LinkAttachmentAsync(page, id, Ct);
        return id;
    }

    [Fact]
    public async Task A_new_thumbnail_replaces_the_old_one_and_hands_back_the_old_file_to_delete()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var board = await PageAsync(t, a.Id, "board");
        var first = await AttachAsync(t, board, a.Id);
        var second = await AttachAsync(t, board, a.Id);

        var r1 = await Svc(t).SetThumbnailAsync(a.Id, board, first, Ct);
        Assert.True(r1.Success, r1.Error);
        Assert.Null(r1.Data);                    // nothing to delete the first time
        var r2 = await Svc(t).SetThumbnailAsync(a.Id, board, second, Ct);
        Assert.Equal(first, r2.Data);            // the first picture is no longer used
        Assert.Equal(second, t.Verify().NotePages.Single(p => p.Id == board).ThumbnailId);
    }

    [Fact]
    public async Task Only_a_board_can_have_one_and_only_with_its_own_picture()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var doc = await PageAsync(t, a.Id, "page");
        var board = await PageAsync(t, a.Id, "board");
        var other = await PageAsync(t, a.Id, "board");
        var onDoc = await AttachAsync(t, doc, a.Id);
        var pdf = await AttachAsync(t, board, a.Id, "application/pdf");
        var elsewhere = await AttachAsync(t, other, a.Id);

        Assert.Equal("not_a_board", (await Svc(t).SetThumbnailAsync(a.Id, doc, onDoc, Ct)).Error);
        Assert.Equal("invalid_image", (await Svc(t).SetThumbnailAsync(a.Id, board, pdf, Ct)).Error);
        Assert.Equal("invalid_image", (await Svc(t).SetThumbnailAsync(a.Id, board, elsewhere, Ct)).Error);
    }

    [Fact]
    public async Task A_viewer_cannot_set_it_and_setting_it_is_not_an_edit()
    {
        using var t = new TestDb();
        var owner = t.AddUser("o"); var viewer = t.AddUser("v");
        var board = await PageAsync(t, owner.Id, "board");
        await Svc(t).AddShareAsync(owner.Id, board, new AddNoteShareRequest(viewer.Id, null, NoteLevels.View), Ct);
        var img = await AttachAsync(t, board, owner.Id);
        Assert.False((await Svc(t).SetThumbnailAsync(viewer.Id, board, img, Ct)).Success);

        var before = t.Verify().NotePages.Single(p => p.Id == board).UpdatedAt;
        Assert.True((await Svc(t).SetThumbnailAsync(owner.Id, board, img, Ct)).Success);
        Assert.Equal(before, t.Verify().NotePages.Single(p => p.Id == board).UpdatedAt);
    }

    [Fact]
    public async Task The_list_carries_what_a_card_needs_and_hides_text_for_boards()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var board = await PageAsync(t, a.Id, "board");
        var img = await AttachAsync(t, board, a.Id);
        await Svc(t).SetThumbnailAsync(a.Id, board, img, Ct);
        var page = await PageAsync(t, a.Id, "page");
        var db = t.Db;
        db.NotePages.Single(p => p.Id == page).Excerpt = "First lines of the page";
        db.NotePages.Single(p => p.Id == board).Excerpt = "should never show";
        await db.SaveChangesAsync();

        var list = (await Svc(t).GetSidebarAsync(a.Id, Ct)).Pages;
        var b = list.Single(p => p.Id == board);
        Assert.Equal(img, b.ThumbnailId);
        Assert.NotNull(b.ThumbnailAt);
        Assert.Null(b.Excerpt);
        Assert.Equal("First lines of the page", list.Single(p => p.Id == page).Excerpt);
        Assert.Equal(a.Nik, list.Single(p => p.Id == page).OwnerNik);
    }
}
