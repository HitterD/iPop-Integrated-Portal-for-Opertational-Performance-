using IPop.Application.Attachments;
using IPop.Application.Documents;
using IPop.Application.Notes;
using IPop.Core.Entities;
using IPop.Infrastructure.Documents;
using IPop.Infrastructure.Notes;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// File cards in Notes: preview-info follows page access, is made once (convert + page-1 picture) and remembered, survives a
/// failed conversion, refuses huge files, and its derived files live and die with the page like any other file.
public class NoteFilePreviewTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// Keeps bytes in memory but writes real Attachment rows, like the NAS store.
    private sealed class MemStore(TestDb t) : IAttachmentStore
    {
        public Dictionary<Guid, byte[]> Bytes { get; } = [];
        public List<Guid> Deleted { get; } = [];
        public int Puts;
        public async Task<StoredAttachment> PutAsync(string area, string fileName, string contentType, Stream content, Guid uploaderId, CancellationToken ct)
        {
            Puts++;
            using var ms = new MemoryStream(); await content.CopyToAsync(ms, ct);
            var id = Guid.NewGuid();
            Bytes[id] = ms.ToArray();
            await using var db = t.Verify();
            db.Attachments.Add(new Attachment { Id = id, Area = area, FileName = fileName, ContentType = contentType, Size = ms.Length, Sha256 = "x", StoragePath = "local:" + id, UploaderId = uploaderId, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return new StoredAttachment(id, fileName, contentType, ms.Length, "x");
        }
        public Task<(StoredAttachment Meta, Stream Content)?> OpenAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<(StoredAttachment, Stream)?>(Bytes.TryGetValue(id, out var b) ? (new StoredAttachment(id, "f", "x", b.Length, "x"), new MemoryStream(b)) : null);
        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            Deleted.Add(id);
            await using var db = t.Verify();
            await db.Attachments.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, DateTime.UtcNow), ct);
        }
    }

    private sealed class FakeDocs : IWordDocumentService, IPresentationService, IPdfService, ISpreadsheetService
    {
        public int WordCalls, SlideCalls, RenderCalls, SheetCalls;
        public bool Fail;
        public int Pages = 4;
        private static readonly byte[] Pdf = "%PDF-fake"u8.ToArray();
        public Task<byte[]> ConvertToPdfAsync(Stream s, CancellationToken ct = default)
        { WordCalls++; return Fail ? throw new InvalidDataException("broken") : Task.FromResult(Pdf); }
        Task<byte[]> IPresentationService.ConvertToPdfAsync(Stream s, CancellationToken ct)
        { SlideCalls++; return Fail ? throw new InvalidDataException("broken") : Task.FromResult(Pdf); }
        public Task<PdfFirstPage> RenderFirstPageAsync(Stream s, int edge, CancellationToken ct = default)
        { RenderCalls++; return Fail ? throw new InvalidDataException("broken") : Task.FromResult(new PdfFirstPage(Pages, [0xFF, 0xD8, 1, 2])); }
        public Task<int> GetSheetCountAsync(Stream s, string fileName, CancellationToken ct = default) { SheetCalls++; return Task.FromResult(3); }
        public Task<byte[]> CreateSampleDocumentAsync(string title, string content, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> ExtractTextAsync(Stream s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> ReplaceTextAsync(Stream s, IDictionary<string, string> r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> CreateSamplePresentationAsync(string t, string s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetSlideCountAsync(Stream s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> CreateSamplePdfAsync(string t, string m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> MergePdfAsync(IEnumerable<Stream> s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetPageCountAsync(Stream s, CancellationToken ct = default) => throw new NotSupportedException();
        Task<string> IPdfService.ExtractTextAsync(Stream s, CancellationToken ct) => throw new NotSupportedException();
        Task<byte[]> ISpreadsheetService.ConvertToPdfAsync(Stream s, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> CreateSampleSpreadsheetAsync(string n, IEnumerable<string[]> r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<string[]>> ReadSheetDataAsync(Stream s, string? n = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static NoteFilePreviewService Svc(TestDb t, IAttachmentStore store, FakeDocs d) =>
        new(t.Verify(), new NoteService(t.Verify()), store, d, d, d, d);

    private static async Task<Guid> PageAsync(TestDb t, Guid owner)
        => (await new NoteService(t.Verify()).CreateAsync(owner, new CreateNoteRequest(null, "P", null, null, null, null), Ct)).Data!.Id;

    private static async Task<Guid> FileAsync(TestDb t, MemStore store, Guid page, Guid owner, string name, string type, long? size = null)
    {
        var s = await store.PutAsync(AttachmentAreas.Notes, name, type, new MemoryStream([1, 2, 3]), owner, Ct);
        if (size is { } big) await t.Verify().Attachments.Where(a => a.Id == s.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.Size, big));
        await new NoteService(t.Verify()).LinkAttachmentAsync(page, s.Id, Ct);
        store.Puts = 0;
        return s.Id;
    }

    [Fact]
    public async Task A_word_file_is_converted_once_and_its_pdf_and_page_one_picture_are_linked_to_the_page()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var store = new MemStore(t);
        var file = await FileAsync(t, store, page, a.Id, "Notulen.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        var docs = new FakeDocs();

        var r = await Svc(t, store, docs).GetAsync(a.Id, page, file, Ct);
        Assert.True(r.Success);
        Assert.Equal("word", r.Data!.Kind);
        Assert.Equal(4, r.Data.Pages);
        Assert.Equal("pages", r.Data.Unit);
        Assert.True(r.Data.Previewable);
        Assert.NotNull(r.Data.PdfId);
        Assert.NotNull(r.Data.ThumbnailId);
        var linked = await t.Verify().NoteAttachments.Where(x => x.PageId == page).Select(x => x.AttachmentId).ToListAsync();
        Assert.Contains(r.Data.PdfId!.Value, linked);
        Assert.Contains(r.Data.ThumbnailId!.Value, linked);

        // second view (another request/person): nothing is converted or stored again
        var again = await Svc(t, store, docs).GetAsync(a.Id, page, file, Ct);
        Assert.Equal(r.Data, again.Data);
        Assert.Equal(1, docs.WordCalls);
        Assert.Equal(1, docs.RenderCalls);
        Assert.Equal(2, store.Puts);
        Assert.Equal(1, await t.Verify().NoteFilePreviews.CountAsync());
    }

    [Fact]
    public async Task Two_viewers_opening_the_same_file_at_once_convert_it_only_once()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var store = new MemStore(t);
        var file = await FileAsync(t, store, page, a.Id, "Deck.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation");
        var docs = new FakeDocs();

        var results = await Task.WhenAll(Svc(t, store, docs).GetAsync(a.Id, page, file, Ct), Svc(t, store, docs).GetAsync(a.Id, page, file, Ct));
        Assert.All(results, r => Assert.Equal("slides", r.Data!.Kind));
        Assert.Equal(results[0].Data, results[1].Data);
        Assert.Equal(1, docs.SlideCalls);
    }

    [Fact]
    public async Task Someone_who_cannot_open_the_page_or_a_file_that_is_not_in_the_page_gets_not_found()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var b = t.AddUser("b");
        var page = await PageAsync(t, a.Id);
        var other = await PageAsync(t, a.Id);
        var store = new MemStore(t);
        var file = await FileAsync(t, store, page, a.Id, "x.pdf", "application/pdf");
        var elsewhere = await FileAsync(t, store, other, a.Id, "y.pdf", "application/pdf");
        var docs = new FakeDocs();

        Assert.Equal("not_found", (await Svc(t, store, docs).GetAsync(b.Id, page, file, Ct)).Error);        // no access to the page
        Assert.Equal("not_found", (await Svc(t, store, docs).GetAsync(a.Id, page, elsewhere, Ct)).Error);   // file of another page
        Assert.Equal("not_found", (await Svc(t, store, docs).GetAsync(a.Id, page, Guid.NewGuid(), Ct)).Error);
        Assert.Equal(0, docs.RenderCalls);
        Assert.Equal(0, await t.Verify().NoteFilePreviews.CountAsync());
    }

    [Fact]
    public async Task A_conversion_that_fails_is_remembered_and_the_card_says_there_is_no_preview()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var store = new MemStore(t);
        var file = await FileAsync(t, store, page, a.Id, "broken.pptx", "application/octet-stream");
        var docs = new FakeDocs { Fail = true };

        var r = await Svc(t, store, docs).GetAsync(a.Id, page, file, Ct);
        Assert.True(r.Success);
        Assert.Equal("slides", r.Data!.Kind);
        Assert.Equal("conversion_failed", r.Data.Error);
        Assert.False(r.Data.Previewable);
        Assert.Null(r.Data.Pages);
        Assert.Null(r.Data.ThumbnailId);
        await Svc(t, store, docs).GetAsync(a.Id, page, file, Ct);
        Assert.Equal(1, docs.SlideCalls);   // not retried on every view
    }

    [Fact]
    public async Task A_file_over_60_MB_is_not_converted_but_still_gets_a_card()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var store = new MemStore(t);
        var file = await FileAsync(t, store, page, a.Id, "huge.pdf", "application/pdf", size: NoteFilePreviewService.MaxConvertBytes + 1);
        var docs = new FakeDocs();

        var r = await Svc(t, store, docs).GetAsync(a.Id, page, file, Ct);
        Assert.Equal("pdf", r.Data!.Kind);
        Assert.Null(r.Data.Pages);
        Assert.Equal("too_large", r.Data.Error);
        Assert.True(r.Data.Previewable);   // the browser shows a PDF itself
        Assert.Equal(0, docs.RenderCalls);
    }

    [Fact]
    public async Task Excel_counts_sheets_images_are_their_own_thumbnail_and_old_ppt_is_download_only()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var store = new MemStore(t);
        var xlsx = await FileAsync(t, store, page, a.Id, "Budget.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var png = await FileAsync(t, store, page, a.Id, "pic.png", "image/png");
        var ppt = await FileAsync(t, store, page, a.Id, "old.ppt", "application/vnd.ms-powerpoint");
        var docs = new FakeDocs();
        var svc = Svc(t, store, docs);

        var x = (await svc.GetAsync(a.Id, page, xlsx, Ct)).Data!;
        Assert.Equal(("sheet", 3, "sheets", true), (x.Kind, x.Pages, x.Unit, x.Previewable));
        var p = (await svc.GetAsync(a.Id, page, png, Ct)).Data!;
        Assert.Equal(("image", (Guid?)png), (p.Kind, p.ThumbnailId));
        var o = (await svc.GetAsync(a.Id, page, ppt, Ct)).Data!;
        Assert.Equal(("none", false), (o.Kind, o.Previewable));
        Assert.Equal(0, docs.SlideCalls + docs.WordCalls);
        Assert.Equal(0, store.Puts);
    }

    [Fact]
    public async Task Purging_the_page_removes_the_file_its_preview_files_and_the_remembered_preview()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var store = new MemStore(t);
        var file = await FileAsync(t, store, page, a.Id, "Notulen.docx", "application/msword");
        var info = (await Svc(t, store, new FakeDocs()).GetAsync(a.Id, page, file, Ct)).Data!;

        var notes = new NoteService(t.Verify());
        await notes.TrashAsync(a.Id, page, Ct);
        var orphans = (await new NoteService(t.Verify()).PurgeWithOrphansAsync(a.Id, page, Ct)).Data!;
        Assert.Contains(info.PdfId!.Value, orphans);
        Assert.Contains(info.ThumbnailId!.Value, orphans);
        await NotesTrashWorker.DeleteOrphanAttachmentsAsync(t.Verify(), store, orphans, NullLogger.Instance, Ct);
        Assert.Contains(file, store.Deleted);
        Assert.Contains(info.PdfId!.Value, store.Deleted);
        Assert.Contains(info.ThumbnailId!.Value, store.Deleted);
        Assert.False(await t.Verify().NoteFilePreviews.AnyAsync());
    }

    [Fact]
    public async Task Preview_pictures_are_not_offered_as_page_images_for_the_cover()
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var page = await PageAsync(t, a.Id);
        var store = new MemStore(t);
        var file = await FileAsync(t, store, page, a.Id, "x.pdf", "application/pdf");
        var info = (await Svc(t, store, new FakeDocs()).GetAsync(a.Id, page, file, Ct)).Data!;
        await t.Verify().Attachments.Where(x => x.Id == info.ThumbnailId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContentType, "image/jpeg"));

        var images = (await new NoteService(t.Verify()).GetPageImagesAsync(a.Id, page, Ct)).Data!;
        Assert.DoesNotContain(images, i => i.Id == info.ThumbnailId);
    }

    /// Real DevExpress: a Word file becomes a PDF with a page count and a JPEG of page 1 (Skia engine, as in production).
    [Fact]
    public async Task Real_conversion_draws_page_one_of_a_word_document()
    {
        new ServiceCollection().AddDevExpressDocumentServices();
        var factory = new DevExpressDocumentFactory();
        var word = new WordDocumentService(factory);
        var pdf = new PdfService(factory);
        var docx = await word.CreateSampleDocumentAsync("Notulen rapat", "Isi rapat ICT.");
        var bytes = await word.ConvertToPdfAsync(new MemoryStream(docx));
        var first = await pdf.RenderFirstPageAsync(new MemoryStream(bytes), NoteFilePreviewService.ThumbEdge);
        Assert.Equal(1, first.Pages);
        Assert.NotNull(first.Jpeg);
        Assert.True(first.Jpeg!.Length > 500);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, first.Jpeg[..2]);   // JPEG signature
        using var img = SixLabors.ImageSharp.Image.Load(first.Jpeg);
        Assert.Equal(NoteFilePreviewService.ThumbEdge, Math.Max(img.Width, img.Height));
    }
}
