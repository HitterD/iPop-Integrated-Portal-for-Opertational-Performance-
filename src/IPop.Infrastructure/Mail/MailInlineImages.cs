using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using IPop.Application.Attachments;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IPop.Infrastructure.Mail;

/// Pasted images used to live in Mails.Body as data: URIs (most of the database). Every body
/// save now moves them to the attachment store (NAS) and points the tag at
/// <c>/api/attachments/{id}</c> with <c>data-ipop-att="{id}"</c>; <see cref="MailInlineImage"/>
/// rows link each image to the mails that show it, which is what MailAttachmentPolicy reads.
public sealed partial class MailInlineImages(IPopDbContext db, IAttachmentStore? store, ILogger? log = null)
{
    public const string Attr = "data-ipop-att";
    private ILogger Log => log ?? NullLogger.Instance;

    [GeneratedRegex(@"^data:image/(png|jpeg|gif|webp);base64,([A-Za-z0-9+/=\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DataImage();

    // Relative or absolute (the reader may have copied the API origin into a paste).
    [GeneratedRegex(@"^(?:https?://[^/""'\s]+)?/api/attachments/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/?$")]
    private static partial Regex AttachmentUrl();

    public static string Src(Guid id) => $"/api/attachments/{id}";

    public sealed record Result(string Html, IReadOnlyList<Guid> Ids, int Stored, int Failed);

    /// Replaces each data:image src with a stored attachment (re-using an identical image the
    /// uploader already stored, so draft autosaves do not upload it again) and normalizes tags
    /// that already point at an attachment. A data: URI is only replaced after the store
    /// succeeded; on failure it stays inline. Returns the body unchanged when nothing applies.
    public async Task<Result> ExtractAsync(string? html, Guid uploaderId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains("<img", StringComparison.OrdinalIgnoreCase))
            return new Result(html ?? "", [], 0, 0);

        var doc = new HtmlParser().ParseDocument("<!doctype html><html><body>" + html + "</body></html>");
        var body = doc.Body;
        if (body is null) return new Result(html, [], 0, 0);

        var ids = new List<Guid>();
        var stored = 0;
        var failed = 0;
        var changed = false;
        var n = 0;
        foreach (var img in body.QuerySelectorAll("img").ToList())
        {
            var src = img.GetAttribute("src") ?? "";
            if (DataImage().Match(src) is { Success: true } m)
            {
                if (store is null) continue;
                var id = await StoreAsync(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value, uploaderId, ++n, ct);
                if (id is null) { failed++; continue; }
                Point(img, id.Value);
                ids.Add(id.Value);
                stored++;
                changed = true;
            }
            else if (IdOf(img) is { } id)
            {
                // The composer shows these through blob: URLs; whatever src came back, the id wins.
                if (img.GetAttribute("src") != Src(id) || img.GetAttribute(Attr) != id.ToString()) { Point(img, id); changed = true; }
                ids.Add(id);
            }
        }
        return new Result(changed ? body.InnerHtml : html, ids.Distinct().ToList(), stored, failed);
    }

    private static Guid? IdOf(IElement img)
    {
        if (AttachmentUrl().Match(img.GetAttribute("src") ?? "") is { Success: true } m) return Guid.Parse(m.Groups[1].Value);
        return Guid.TryParse(img.GetAttribute(Attr), out var id) ? id : null;
    }

    private static void Point(IElement img, Guid id)
    {
        img.SetAttribute("src", Src(id));
        img.SetAttribute(Attr, id.ToString());
    }

    private async Task<Guid?> StoreAsync(string kind, string base64, Guid uploaderId, int n, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
        if (bytes.Length == 0) return null;

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var existing = await db.Attachments.AsNoTracking()
            .Where(a => a.Area == AttachmentAreas.Mail && a.UploaderId == uploaderId && a.Sha256 == sha && a.DeletedAt == null)
            .Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var ext = kind == "jpeg" ? "jpg" : kind;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var put = await store!.PutAsync(AttachmentAreas.Mail, $"image-{n}.{ext}", "image/" + kind, ms, uploaderId, ct);
            return put.Id;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.LogWarning(ex, "Inline mail image ({Bytes} bytes) could not be stored; it stays inline in the body.", bytes.Length);
            return null;
        }
    }

    /// Ids <paramref name="userId"/> may put in a mail: their own mail-area uploads, or images
    /// already linked to a mail they can read (reply/forward). Anything else is never linked.
    public async Task<HashSet<Guid>> LinkableAsync(Guid userId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var own = await db.Attachments.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.Area == AttachmentAreas.Mail && a.DeletedAt == null && a.UploaderId == userId)
            .Select(a => a.Id).ToListAsync(ct);
        var rest = ids.Except(own).ToList();
        var shared = rest.Count == 0 ? [] : await ReadableLinks(db, userId)
            .Where(l => rest.Contains(l.AttachmentId))
            .Select(l => l.AttachmentId).Distinct().ToListAsync(ct);
        return [.. own, .. shared];
    }

    /// Links on mails the user takes part in (any recipient row, including "from") that were not recalled.
    internal static IQueryable<MailInlineImage> ReadableLinks(IPopDbContext db, Guid userId) =>
        db.MailInlineImages.AsNoTracking().Where(l =>
            db.MailRecipients.Any(r => r.MailId == l.MailId && r.UserId == userId)
            && db.Mails.Any(m => m.Id == l.MailId && m.Status != "recalled"));

    /// Records which images <paramref name="mailId"/> shows. Ids the user may not link are
    /// dropped. With <paramref name="replace"/> (draft saves) links no longer in the body go.
    /// Adds to the change tracker only; the caller saves.
    public async Task LinkAsync(Guid mailId, Guid userId, IReadOnlyList<Guid> ids, bool replace, CancellationToken ct)
    {
        var allowed = await LinkableAsync(userId, ids, ct);
        var current = await db.MailInlineImages.Where(l => l.MailId == mailId).ToListAsync(ct);
        if (replace) db.MailInlineImages.RemoveRange(current.Where(l => !allowed.Contains(l.AttachmentId)));
        foreach (var id in allowed.Where(id => current.All(l => l.AttachmentId != id)))
            db.MailInlineImages.Add(new MailInlineImage { MailId = mailId, AttachmentId = id });
    }

    /// For PDF/print, which render server-side without the reader's token: images linked to
    /// <paramref name="mailId"/> go back in as data: URIs. Unlinked or unreadable ones are left as is.
    public static async Task<string> InlineForExportAsync(IPopDbContext db, IAttachmentStore store, Guid mailId, string? html, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains(Attr, StringComparison.Ordinal)) return html ?? "";
        var linked = (await db.MailInlineImages.AsNoTracking().Where(l => l.MailId == mailId)
            .Select(l => l.AttachmentId).ToListAsync(ct)).ToHashSet();
        if (linked.Count == 0) return html;

        var doc = new HtmlParser().ParseDocument("<!doctype html><html><body>" + html + "</body></html>");
        var body = doc.Body;
        if (body is null) return html;
        var cache = new Dictionary<Guid, string?>();
        foreach (var img in body.QuerySelectorAll($"img[{Attr}]").ToList())
        {
            if (IdOf(img) is not { } id || !linked.Contains(id)) continue;
            if (!cache.TryGetValue(id, out var uri))
            {
                try
                {
                    var opened = await store.OpenAsync(id, ct);
                    if (opened is { } o)
                    {
                        await using var s = o.Content;
                        using var ms = new MemoryStream();
                        await s.CopyToAsync(ms, ct);
                        uri = $"data:{o.Meta.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}";
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { uri = null; }
                cache[id] = uri;
            }
            if (uri is not null) img.SetAttribute("src", uri);
        }
        return body.InnerHtml;
    }

    public sealed record BackfillResult(int Mails, int Updated, int Images, int Failed, Guid? Next);

    /// One batch of the one-off move of existing bodies: mails whose Body still holds a data:image,
    /// in Id order after <paramref name="after"/>. The mail's sender is the uploader. Each mail is
    /// saved on its own, so a stop or crash loses nothing and a rerun only finds what is left
    /// (an image that failed to store stays inline and is reported in Failed).
    public async Task<BackfillResult> BackfillBatchAsync(int batch, Guid? after, CancellationToken ct)
    {
        batch = Math.Clamp(batch, 1, 500);
        var q = db.Mails.Where(m => m.IsHtml && m.Body.Contains("data:image"));
        if (after is { } a) q = q.Where(m => m.Id.CompareTo(a) > 0);
        var ids = await q.OrderBy(m => m.Id).Select(m => m.Id).Take(batch).ToListAsync(ct);

        int updated = 0, images = 0, failed = 0;
        foreach (var id in ids)
        {
            var mail = await db.Mails.FirstAsync(m => m.Id == id, ct);
            var r = await ExtractAsync(mail.Body, mail.SenderId, ct);
            images += r.Stored;
            failed += r.Failed;
            // Same rule as a live save, with the sender as the saver: what was just stored is theirs.
            if (r.Ids.Count > 0) await LinkAsync(id, mail.SenderId, r.Ids, replace: false, ct);
            if (r.Html != mail.Body) { mail.Body = r.Html; updated++; }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear(); // bodies are large; don't keep a batch of them tracked
            Log.LogInformation("Mail inline images: mail {MailId} — {Stored} stored, {Failed} failed.", id, r.Stored, r.Failed);
        }
        return new BackfillResult(ids.Count, updated, images, failed, ids.Count == batch ? ids[^1] : null);
    }
}
