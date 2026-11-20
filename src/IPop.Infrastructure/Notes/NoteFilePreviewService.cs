using IPop.Application.Attachments;
using IPop.Application.Documents;
using IPop.Application.Notes;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IPop.Infrastructure.Notes;

/// The file card in a Notes page: what kind of file it is, how many pages, a picture of page 1 and (Word/PowerPoint) the file
/// as PDF for the viewer. Made once, on the first view, and remembered in <see cref="NoteFilePreview"/>; the derived files are
/// stored like any Notes upload and linked to the page, so the same people can open them and they are purged with the page.
public sealed class NoteFilePreviewService(
    IPopDbContext db, INoteService notes, IAttachmentStore store,
    IWordDocumentService word, IPresentationService slides, IPdfService pdf, ISpreadsheetService sheets,
    ILogger<NoteFilePreviewService>? log = null)
{
    /// Larger files are not converted (CPU and memory on the API host); the card still works, only without pages/thumbnail.
    public const long MaxConvertBytes = 60L * 1024 * 1024;
    public const int ThumbEdge = 480;

    // Same file opened by two people at once: one converts, the other waits and reads the result. Across API replicas the
    // primary key on NoteFilePreview decides (the loser drops its copy). Conversions are heavy: at most two at a time.
    private static readonly SemaphoreSlim[] Stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly SemaphoreSlim Converting = new(2, 2);
    private readonly ILogger log = (ILogger?)log ?? NullLogger.Instance;

    public static string KindOf(string fileName, string? contentType)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        var ct = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (ext == ".pdf" || ct == "application/pdf") return "pdf";
        if (ext is ".docx" or ".doc" or ".rtf" or ".odt") return "word";
        if (ext == ".pptx") return "slides";
        if (ext is ".ppt" or ".pps" or ".odp") return "none";   // legacy PowerPoint: no converter
        if (ext is ".xlsx" or ".xls" or ".xlsm" or ".csv" or ".ods") return "sheet";
        if (ext is ".heic" or ".heif" or ".svg" || ct is "image/heic" or "image/heif" or "image/svg+xml") return "none";
        if (ct.StartsWith("image/")) return "image";
        if (ct.StartsWith("video/")) return "video";
        if (ct.StartsWith("audio/")) return "audio";
        if (ext is ".txt" or ".md" or ".log" or ".json" or ".xml" or ".yml" or ".yaml" || ct.StartsWith("text/") || ct is "application/json")
            return "text";
        return "none";
    }

    public async Task<NoteResult<NoteFilePreviewDto>> GetAsync(Guid userId, Guid pageId, Guid attachmentId, CancellationToken ct)
    {
        if (await notes.GetAccessAsync(userId, pageId, ct) == NoteAccess.None) return Fail("not_found");
        // only a file that is IN this page: page access must not open any other notes file by guessing its id
        var att = await (from na in db.NoteAttachments.AsNoTracking()
                         join a in db.Attachments.AsNoTracking() on na.AttachmentId equals a.Id
                         where na.PageId == pageId && na.AttachmentId == attachmentId && a.DeletedAt == null && a.Area == AttachmentAreas.Notes
                         select new { a.Id, a.FileName, a.ContentType, a.Size, a.UploaderId }).FirstOrDefaultAsync(ct);
        if (att is null) return Fail("not_found");
        var kind = KindOf(att.FileName, att.ContentType);

        var stripe = Stripes[(attachmentId.GetHashCode() & 0x7fffffff) % Stripes.Length];
        await stripe.WaitAsync(ct);
        try
        {
            var row = await db.NoteFilePreviews.AsNoTracking().FirstOrDefaultAsync(p => p.AttachmentId == attachmentId, ct);
            if (row is not null && !await DerivedAliveAsync(row, ct))
            {
                // its picture/PDF went with another page that was purged: make them again
                await db.NoteFilePreviews.Where(p => p.AttachmentId == attachmentId).ExecuteDeleteAsync(ct);
                row = null;
            }
            row ??= await CreateAsync(att.Id, att.FileName, att.ContentType, att.Size, att.UploaderId, kind, pageId, ct);
            await LinkAsync(pageId, [row.ThumbAttachmentId, row.PdfAttachmentId], ct);
            return new NoteResult<NoteFilePreviewDto>(ToDto(row, attachmentId), null);
        }
        finally { stripe.Release(); }
    }

    private static NoteFilePreviewDto ToDto(NoteFilePreview r, Guid attachmentId)
    {
        var thumb = r.Kind == "image" ? attachmentId : r.ThumbAttachmentId;
        var previewable = r.Kind switch
        {
            "none" => false,
            "word" => r.PdfAttachmentId is not null || r.Error is null,   // .docx also renders in the browser without the PDF
            "slides" => r.PdfAttachmentId is not null,
            _ => true,
        };
        var unit = r.Pages is null ? null : r.Kind == "sheet" ? "sheets" : "pages";
        return new NoteFilePreviewDto(r.Kind, r.Pages, unit, thumb, r.PdfAttachmentId, previewable, r.Error);
    }

    private async Task<bool> DerivedAliveAsync(NoteFilePreview r, CancellationToken ct)
    {
        var ids = new[] { r.ThumbAttachmentId, r.PdfAttachmentId }.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (ids.Count == 0) return true;
        return await db.Attachments.AsNoTracking().CountAsync(a => ids.Contains(a.Id) && a.DeletedAt == null, ct) == ids.Count;
    }

    private async Task LinkAsync(Guid pageId, Guid?[] ids, CancellationToken ct)
    {
        var want = ids.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (want.Count == 0) return;
        var have = await db.NoteAttachments.AsNoTracking().Where(a => a.PageId == pageId && want.Contains(a.AttachmentId))
            .Select(a => a.AttachmentId).ToListAsync(ct);
        var add = want.Except(have).ToList();
        if (add.Count == 0) return;
        foreach (var id in add) db.NoteAttachments.Add(new NoteAttachment { AttachmentId = id, PageId = pageId });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); }   // linked meanwhile by another request
    }

    private async Task<NoteFilePreview> CreateAsync(Guid id, string fileName, string contentType, long size, Guid uploaderId, string kind,
        Guid pageId, CancellationToken ct)
    {
        var row = new NoteFilePreview { AttachmentId = id, Kind = kind, CreatedAt = DateTime.UtcNow };
        var made = new List<Guid>();
        if (kind is "pdf" or "word" or "slides" or "sheet")
        {
            if (size > MaxConvertBytes) row.Error = "too_large";
            else
            {
                await Converting.WaitAsync(ct);
                try { await ConvertAsync(row, id, fileName, kind, uploaderId, made, ct); }
                catch (Exception ex) when (IsStorageProblem(ex))
                {
                    // the NAS is away: nothing is remembered, the next view tries again
                    await DropAsync(made);
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // the file itself can't be read/converted: remembered, so it is not tried again on every view
                    log.LogWarning(ex, "Notes file preview: couldn't prepare {File} ({Id})", fileName, id);
                    if (kind != "sheet") row.Error = ex is AttachmentRejectedException ? "too_large" : "conversion_failed";   // a sheet still previews in the browser, only uncounted
                }
                finally { Converting.Release(); }
            }
        }

        db.NoteFilePreviews.Add(row);
        foreach (var d in made) db.NoteAttachments.Add(new NoteAttachment { AttachmentId = d, PageId = pageId });
        try
        {
            await db.SaveChangesAsync(ct);
            return row;
        }
        catch (DbUpdateException)
        {
            // another API replica made it first: keep theirs, drop ours
            db.ChangeTracker.Clear();
            await DropAsync(made);
            return await db.NoteFilePreviews.AsNoTracking().FirstAsync(p => p.AttachmentId == id, ct);
        }
    }

    private static bool IsStorageProblem(Exception ex) =>
        ex is IPop.Application.Files.SynoException or FileNotFoundException;

    private async Task DropAsync(List<Guid> made)
    {
        foreach (var d in made)
        {
            try { await store.DeleteAsync(d, CancellationToken.None); }
            catch (Exception ex) { log.LogWarning(ex, "Notes file preview: couldn't remove derived file {Id}", d); }
        }
        made.Clear();
    }

    private async Task ConvertAsync(NoteFilePreview row, Guid id, string fileName, string kind, Guid uploaderId, List<Guid> made, CancellationToken ct)
    {
        var opened = await store.OpenAsync(id, ct) ?? throw new FileNotFoundException("attachment bytes missing", fileName);
        using var buffer = new MemoryStream();
        await using (var content = opened.Content) await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var baseName = Path.GetFileNameWithoutExtension(fileName);

        if (kind == "sheet")
        {
            row.Pages = await sheets.GetSheetCountAsync(buffer, fileName, ct);
            return;
        }

        byte[] pdfBytes;
        if (kind == "pdf") pdfBytes = buffer.ToArray();
        else
        {
            pdfBytes = kind == "slides" ? await slides.ConvertToPdfAsync(buffer, ct) : await word.ConvertToPdfAsync(buffer, ct);
            pdfBytes = IPop.Infrastructure.Documents.PdfTitle.Set(pdfBytes, fileName);
            using var pdfStream = new MemoryStream(pdfBytes, writable: false);
            var stored = await store.PutAsync(AttachmentAreas.Notes, baseName + ".pdf", "application/pdf", pdfStream, uploaderId, ct);
            made.Add(stored.Id);
            row.PdfAttachmentId = stored.Id;
        }

        using var forRender = new MemoryStream(pdfBytes, writable: false);
        var first = await pdf.RenderFirstPageAsync(forRender, ThumbEdge, ct);
        row.Pages = first.Pages > 0 ? first.Pages : null;
        if (first.Jpeg is { Length: > 0 } jpeg)
        {
            using var thumbStream = new MemoryStream(jpeg, writable: false);
            var thumb = await store.PutAsync(AttachmentAreas.Notes, baseName + " (page 1).jpg", "image/jpeg", thumbStream, uploaderId, ct);
            made.Add(thumb.Id);
            row.ThumbAttachmentId = thumb.Id;
        }
    }

    /// Ids of pictures/PDFs made for previews (never the files people attached): kept out of "images in this page" and copies.
    public static IQueryable<Guid> DerivedIds(IPopDbContext db) =>
        db.NoteFilePreviews.Where(p => p.ThumbAttachmentId != null).Select(p => p.ThumbAttachmentId!.Value)
            .Concat(db.NoteFilePreviews.Where(p => p.PdfAttachmentId != null).Select(p => p.PdfAttachmentId!.Value));

    private static NoteResult<NoteFilePreviewDto> Fail(string code) => new(null, code);
}
