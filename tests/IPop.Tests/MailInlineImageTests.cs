using System.Security.Cryptography;
using IPop.Application.Attachments;
using IPop.Application.Mail;
using IPop.Core.Entities;
using IPop.Infrastructure.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Pasted images leave Mails.Body for the attachment store (NAS): every save swaps data: URIs
/// for /api/attachments/{id}, links the image to the mail, and MailAttachmentPolicy lets exactly
/// that mail's participants read it.
public sealed class MailInlineImageTests
{
    private static readonly byte[] PngA = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];
    private static readonly byte[] PngB = [0x89, 0x50, 0x4E, 0x47, 9, 9, 9, 9];
    private static string DataUri(byte[] b) => "data:image/png;base64," + Convert.ToBase64String(b);
    private static string Body(params byte[][] imgs) =>
        "<p>hi</p>" + string.Concat(imgs.Select(b => $"<img src=\"{DataUri(b)}\" alt=\"shot\" width=\"40\" style=\"max-width: 100%\">"));

    private static ComposeMailRequest Req(string body, params string[] to) => new("Subj", body, [.. to], [], []);

    private static MailService Svc(TestDb t, IAttachmentStore store) =>
        new(t.Verify(), attachments: store);

    [Fact]
    public async Task Send_replaces_data_uris_with_stored_attachments_and_links_them()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var store = new FakeStore(t);

        var res = await Svc(t, store).SendAsync(a.Id, Req(Body(PngA, PngB), b.Nik), default);

        Assert.True(res.Success);
        await using var v = t.Verify();
        var mail = await v.Mails.SingleAsync(m => m.Id == res.MailId);
        Assert.DoesNotContain("data:image", mail.Body);
        var atts = await v.Attachments.ToListAsync();
        Assert.Equal(2, atts.Count);
        Assert.All(atts, x => { Assert.Equal(AttachmentAreas.Mail, x.Area); Assert.Equal(a.Id, x.UploaderId); });
        Assert.Equal(PngA, store.Blobs[atts.Single(x => x.Sha256 == Sha(PngA)).Id]);
        foreach (var x in atts)
        {
            Assert.Contains($"src=\"/api/attachments/{x.Id}\"", mail.Body);
            Assert.Contains($"data-ipop-att=\"{x.Id}\"", mail.Body);
        }
        Assert.Contains("alt=\"shot\"", mail.Body);
        Assert.Contains("width=\"40\"", mail.Body);
        Assert.Contains("max-width: 100%", mail.Body);
        var links = await v.MailInlineImages.Where(l => l.MailId == res.MailId).Select(l => l.AttachmentId).ToListAsync();
        Assert.Equal(atts.Select(x => x.Id).OrderBy(x => x), links.OrderBy(x => x));
    }

    [Fact]
    public async Task Repeated_draft_saves_store_the_image_once()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var store = new FakeStore(t);

        var draft = await Svc(t, store).SaveDraftAsync(a.Id, Req(Body(PngA)), default);
        // The composer may send the same pasted image again (e.g. another tab or a client that kept the data: URI).
        for (var i = 0; i < 3; i++)
            Assert.True((await Svc(t, store).UpdateDraftAsync(a.Id, draft.MailId!.Value, Req(Body(PngA)), default)).Success);

        await using var v = t.Verify();
        var att = await v.Attachments.SingleAsync();
        Assert.Equal(1, store.Puts);
        Assert.Equal(att.Id, (await v.MailInlineImages.SingleAsync()).AttachmentId);
        Assert.Contains($"/api/attachments/{att.Id}", (await v.Mails.SingleAsync()).Body);
    }

    [Fact]
    public async Task Draft_save_drops_links_for_images_removed_from_the_body()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var store = new FakeStore(t);
        var draft = await Svc(t, store).SaveDraftAsync(a.Id, Req(Body(PngA)), default);

        await Svc(t, store).UpdateDraftAsync(a.Id, draft.MailId!.Value, Req("<p>no picture</p>"), default);

        await using var v = t.Verify();
        Assert.Empty(await v.MailInlineImages.ToListAsync());
    }

    [Fact]
    public async Task Reply_quoting_a_stored_image_links_it_to_the_reply()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var c = t.AddUser("C");
        var store = new FakeStore(t);
        var first = await Svc(t, store).SendAsync(a.Id, Req(Body(PngA), b.Nik), default);
        Guid att;
        await using (var v0 = t.Verify()) att = (await v0.Attachments.SingleAsync()).Id;

        // B quotes A's image (now /api/attachments/...) and adds C, who was not on the first mail.
        var quoted = $"<p>see below</p><blockquote><img src=\"/api/attachments/{att}\" data-ipop-att=\"{att}\"></blockquote>";
        var reply = await Svc(t, store).SendAsync(b.Id, Req(quoted, a.Nik, c.Nik) with { InReplyToId = first.MailId }, default);

        Assert.True(reply.Success);
        await using var v = t.Verify();
        Assert.True(await v.MailInlineImages.AnyAsync(l => l.MailId == reply.MailId && l.AttachmentId == att));
        Assert.True(await new MailAttachmentPolicy(v).CanReadAsync(att, c.Id, default));
    }

    [Fact]
    public async Task Someone_elses_attachment_cannot_be_linked()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var victim = t.AddUser("V");
        var outsider = t.AddUser("O");
        var store = new FakeStore(t);
        // V mails a picture to nobody but themselves (private), then O tries to pull it into their own mail.
        var priv = await Svc(t, store).SaveDraftAsync(victim.Id, Req(Body(PngA)), default);
        Guid att;
        await using (var v0 = t.Verify()) att = (await v0.Attachments.SingleAsync()).Id;

        var res = await Svc(t, store).SendAsync(outsider.Id,
            Req($"<img src=\"/api/attachments/{att}\" data-ipop-att=\"{att}\">", a.Nik), default);

        Assert.True(res.Success);
        await using var v = t.Verify();
        Assert.False(await v.MailInlineImages.AnyAsync(l => l.MailId == res.MailId));
        var policy = new MailAttachmentPolicy(v);
        Assert.False(await policy.CanReadAsync(att, outsider.Id, default));
        Assert.False(await policy.CanReadAsync(att, a.Id, default));
        Assert.True(await policy.CanReadAsync(att, victim.Id, default));
        Assert.NotNull(priv.MailId);
    }

    [Fact]
    public async Task Policy_allows_sender_and_recipients_and_denies_others()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var c = t.AddUser("C");
        var bcc = t.AddUser("BCC");
        var other = t.AddUser("X");
        var store = new FakeStore(t);
        var res = await Svc(t, store).SendAsync(a.Id,
            new ComposeMailRequest("S", Body(PngA), [b.Nik], [c.Nik], [bcc.Nik]), default);

        await using var v = t.Verify();
        var att = (await v.Attachments.SingleAsync()).Id;
        var policy = new MailAttachmentPolicy(v);
        Assert.Equal("mail", policy.Area);
        foreach (var u in new[] { a, b, c, bcc }) Assert.True(await policy.CanReadAsync(att, u.Id, default), u.Nik);
        Assert.False(await policy.CanReadAsync(att, other.Id, default));
        Assert.True(res.Success);
    }

    [Fact]
    public async Task Policy_denies_recipients_of_a_recalled_mail()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var store = new FakeStore(t);
        var res = await Svc(t, store).SendAsync(a.Id, Req(Body(PngA), b.Nik), default);

        // Mark recalled but keep B's row, so only the Status check stands between B and the image.
        await t.Db.Mails.Where(m => m.Id == res.MailId).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, "recalled"));

        await using var v = t.Verify();
        var att = (await v.Attachments.SingleAsync()).Id;
        Assert.False(await new MailAttachmentPolicy(v).CanReadAsync(att, b.Id, default));
        Assert.True(await new MailAttachmentPolicy(v).CanReadAsync(att, a.Id, default)); // the uploader
    }

    [Fact]
    public async Task Failed_store_keeps_the_image_inline()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var store = new FakeStore(t) { Fail = true };

        var res = await Svc(t, store).SendAsync(a.Id, Req(Body(PngA), b.Nik), default);

        Assert.True(res.Success);
        await using var v = t.Verify();
        Assert.Contains(DataUri(PngA), (await v.Mails.SingleAsync()).Body);
        Assert.Empty(await v.MailInlineImages.ToListAsync());
    }

    [Fact]
    public async Task Backfill_moves_old_bodies_and_is_idempotent()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        // Old mails, written before extraction existed (straight into the table).
        var withImg = await LegacyMail(t, a.Id, b.Id, Body(PngA, PngA));
        var withTwo = await LegacyMail(t, a.Id, b.Id, Body(PngB));
        var plain = await LegacyMail(t, a.Id, b.Id, "<p>text only</p>");
        var store = new FakeStore(t);

        var first = await new MailInlineImages(t.Verify(), store).BackfillBatchAsync(1, null, default);
        Assert.Equal(1, first.Mails);
        Assert.NotNull(first.Next);
        var second = await new MailInlineImages(t.Verify(), store).BackfillBatchAsync(50, first.Next, default);
        Assert.Equal(1, second.Mails);
        Assert.Null(second.Next);
        var again = await new MailInlineImages(t.Verify(), store).BackfillBatchAsync(50, null, default);
        Assert.Equal(0, again.Mails);

        await using var v = t.Verify();
        Assert.Equal(2, store.Puts); // PngA twice in one mail is stored once
        Assert.DoesNotContain(await v.Mails.Select(m => m.Body).ToListAsync(), body => body.Contains("data:image"));
        Assert.Equal("<p>text only</p>", (await v.Mails.SingleAsync(m => m.Id == plain)).Body);
        Assert.Single(await v.MailInlineImages.Where(l => l.MailId == withImg).ToListAsync());
        Assert.Single(await v.MailInlineImages.Where(l => l.MailId == withTwo).ToListAsync());
        Assert.All(await v.Attachments.ToListAsync(), x => Assert.Equal(a.Id, x.UploaderId));
        var att = (await v.MailInlineImages.FirstAsync(l => l.MailId == withImg)).AttachmentId;
        Assert.True(await new MailAttachmentPolicy(v).CanReadAsync(att, b.Id, default));
    }

    [Fact]
    public async Task Backfill_leaves_a_mail_whose_image_failed_for_the_next_run()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var id = await LegacyMail(t, a.Id, b.Id, Body(PngA));

        var failed = await new MailInlineImages(t.Verify(), new FakeStore(t) { Fail = true }).BackfillBatchAsync(50, null, default);
        Assert.Equal(1, failed.Failed);
        await using (var v0 = t.Verify()) Assert.Contains(DataUri(PngA), (await v0.Mails.SingleAsync(m => m.Id == id)).Body);

        var ok = await new MailInlineImages(t.Verify(), new FakeStore(t)).BackfillBatchAsync(50, null, default);
        Assert.Equal(1, ok.Updated);
    }

    [Fact]
    public async Task Export_puts_linked_images_back_as_data_uris()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var store = new FakeStore(t);
        var res = await Svc(t, store).SendAsync(a.Id, Req(Body(PngA), b.Nik), default);

        await using var v = t.Verify();
        var body = (await v.Mails.SingleAsync()).Body;
        var html = await MailInlineImages.InlineForExportAsync(v, store, res.MailId!.Value, body, default);
        Assert.Contains(DataUri(PngA), html);
    }

    [Fact]
    public void Sanitizer_keeps_stored_image_tags_and_drops_bad_ids()
    {
        var id = Guid.NewGuid();
        var html = $"<img src=\"/api/attachments/{id}\" data-ipop-att=\"{id}\" alt=\"x\">";
        Assert.Equal(html, MailHtml.Sanitize(html));
        Assert.Equal(html, MailHtml.ForDisplay(html));
        Assert.Equal("<img src=\"/x.png\">", MailHtml.Sanitize("<img src=\"/x.png\" data-ipop-att=\"nope\">"));
        Assert.Equal("<p>t</p>", MailHtml.Sanitize($"<p data-ipop-att=\"{id}\">t</p>"));
        // Old clients still paste data: images during the migration window.
        var data = $"<img src=\"{DataUri(PngA)}\">";
        Assert.Equal(data, MailHtml.Sanitize(data));
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static async Task<Guid> LegacyMail(TestDb t, Guid sender, Guid to, string body)
    {
        var now = DateTime.UtcNow;
        var m = new Mail { Id = Guid.NewGuid(), SenderId = sender, Subject = "old", Body = body, Status = "sent", CreatedAt = now, SentAt = now };
        t.Db.Mails.Add(m);
        t.Db.MailRecipients.Add(new MailRecipient { Id = Guid.NewGuid(), MailId = m.Id, UserId = sender, RecipientType = "from", Folder = "sent", CreatedAt = now });
        t.Db.MailRecipients.Add(new MailRecipient { Id = Guid.NewGuid(), MailId = m.Id, UserId = to, RecipientType = "to", Folder = "inbox", CreatedAt = now });
        await t.Db.SaveChangesAsync();
        return m.Id;
    }

    /// Mirrors SynologyAttachmentStore: every put writes an Attachments row (TestDb contexts share
    /// one SQLite connection, so the link FK and the dedupe lookup see it).
    private sealed class FakeStore(TestDb t) : IAttachmentStore
    {
        public Dictionary<Guid, byte[]> Blobs { get; } = [];
        public Dictionary<Guid, StoredAttachment> Meta { get; } = [];
        public int Puts;
        public bool Fail;

        public async Task<StoredAttachment> PutAsync(string area, string fileName, string contentType, Stream content, Guid uploaderId, CancellationToken ct)
        {
            if (Fail) throw new IOException("NAS down");
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var id = Guid.NewGuid();
            var m = new StoredAttachment(id, fileName, contentType, ms.Length, Sha(ms.ToArray()));
            Blobs[id] = ms.ToArray();
            Meta[id] = m;
            Puts++;
            await using var db = t.Verify();
            db.Attachments.Add(new Attachment
            {
                Id = id, Area = area, FileName = fileName, ContentType = contentType, Size = m.Size, Sha256 = m.Sha256,
                StoragePath = "local:fake/" + id.ToString("N"), UploaderId = uploaderId, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return m;
        }

        public Task<(StoredAttachment Meta, Stream Content)?> OpenAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Meta.TryGetValue(id, out var m) ? ((StoredAttachment, Stream)?)(m, new MemoryStream(Blobs[id])) : null);

        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    }
}
