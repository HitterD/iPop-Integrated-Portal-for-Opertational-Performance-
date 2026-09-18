using IPop.Application.Mail;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Alias: our own namespace IPop.Infrastructure.Mail shadows the Mail entity type in name lookup.
// Use a distinct alias name (MailEntity) so simple-name resolution inside this namespace
// does not pick the namespace over the entity type.
using MailEntity = IPop.Core.Entities.Mail;

namespace IPop.Infrastructure.Mail;

public sealed class MailService(IPopDbContext db) : IMailService
{
    private const int MaxRecipients = 100;
    private const int MaxSubjectLength = 500;

    // ──────────────────────────────────────────────────────────────────────────
    // Compose & delivery
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<SendMailResult> SendAsync(Guid senderId, ComposeMailRequest req, CancellationToken ct)
    {
        req = Normalize(req);
        var validation = Validate(req);
        if (validation is not null) return validation;

        var recipientUsers = await ResolveRecipientsAsync(req, ct);
        if (recipientUsers is null) return new SendMailResult(false, null, "recipient_not_found");

        var now = DateTime.UtcNow;
        var isScheduled = req.ScheduledAt.HasValue;
        var mail = new MailEntity
        {
            Id = Guid.NewGuid(),
            SenderId = senderId,
            Subject = req.Subject.Trim(),
            Body = req.Body,
            IsHtml = true,
            Signature = req.Signature,
            InReplyToId = req.InReplyToId,
            ThreadRootId = req.ThreadRootId ?? req.InReplyToId, // reply without explicit root telescopes to its parent
            Status = isScheduled ? "scheduled" : "sent",
            Importance = string.IsNullOrWhiteSpace(req.Importance) ? "normal" : req.Importance,
            ScheduledAt = req.ScheduledAt,
            CreatedAt = now,
            SentAt = isScheduled ? null : now,
        };
        if (!isScheduled) await BumpThreadAsync(mail, now, ct);
        db.Mails.Add(mail);

        // Sender's own row lives in Scheduled or Sent.
        db.MailRecipients.Add(MakeRecipient(mail.Id, senderId, "from", isScheduled ? "scheduled" : "sent", now));

        foreach (var u in recipientUsers)
        {
            // Sending to yourself: the sender's row above (Folder=sent) IS the record — adding a
            // second (mail, user) row would trip the unique index, so skip the sender here.
            if (u.Id == senderId) continue;
            var type = req.To.Contains(u.Nik) ? "to" : req.Cc.Contains(u.Nik) ? "cc" : "bcc";
            db.MailRecipients.Add(MakeRecipient(mail.Id, u.Id, type, isScheduled ? "pending" : "inbox", now));
        }

        mail.HasAttachment = await LinkAttachmentsAsync(mail.Id, req.AttachmentIds, ct);
        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mail.Id, null);
    }

    public async Task<SendMailResult> SaveDraftAsync(Guid userId, ComposeMailRequest req, CancellationToken ct)
    {
        req = Normalize(req);
        var validation = ValidateDraft(req);
        if (validation is not null) return validation;

        var now = DateTime.UtcNow;
        var mail = new MailEntity
        {
            Id = Guid.NewGuid(),
            SenderId = userId,
            Subject = req.Subject.Trim(),
            Body = req.Body,
            IsHtml = true,
            Signature = req.Signature,
            InReplyToId = req.InReplyToId,
            ThreadRootId = req.ThreadRootId ?? req.InReplyToId,
            Status = "draft",
            Importance = string.IsNullOrWhiteSpace(req.Importance) ? "normal" : req.Importance,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Mails.Add(mail);
        // Drafts only have the author's row — no recipients are touched.
        db.MailRecipients.Add(MakeRecipient(mail.Id, userId, "from", "draft", now));
        mail.HasAttachment = await LinkAttachmentsAsync(mail.Id, req.AttachmentIds, ct);
        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mail.Id, null);
    }

    public async Task<SendMailResult> UpdateDraftAsync(Guid userId, Guid mailId, ComposeMailRequest req, CancellationToken ct)
    {
        req = Normalize(req);
        var validation = ValidateDraft(req);
        if (validation is not null) return validation;

        var recipient = await db.MailRecipients
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder == "draft", ct);
        if (recipient is null) return new SendMailResult(false, null, "draft_not_found");

        var mail = await db.Mails.FindAsync([mailId], ct);
        if (mail is null) return new SendMailResult(false, null, "draft_not_found");

        mail.Subject = req.Subject.Trim();
        mail.Body = req.Body;
        mail.Signature = req.Signature;
        mail.Importance = string.IsNullOrWhiteSpace(req.Importance) ? "normal" : req.Importance;
        mail.InReplyToId = req.InReplyToId;
        mail.ThreadRootId = req.ThreadRootId ?? req.InReplyToId;
        mail.UpdatedAt = DateTime.UtcNow;
        mail.HasAttachment = await LinkAttachmentsAsync(mail.Id, req.AttachmentIds, ct);
        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> DiscardDraftAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await db.MailRecipients
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder == "draft", ct);
        if (recipient is null) return new SendMailResult(false, null, "draft_not_found");

        db.MailRecipients.Remove(recipient);
        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> ForwardAsync(Guid senderId, Guid originalMailId, ComposeMailRequest req, CancellationToken ct)
    {
        var original = await db.Mails
            .AsNoTracking()
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == originalMailId, ct);
        if (original is null) return new SendMailResult(false, null, "mail_not_found");
        if (!await db.MailRecipients.AnyAsync(r => r.MailId == originalMailId && r.UserId == senderId, ct))
            return new SendMailResult(false, null, "forbidden");

        // Forward is a NEW thread — the frontend has already prefixed the subject.
        var forwarded = req with { ThreadRootId = null, InReplyToId = null };
        var result = await SendAsync(senderId, forwarded, ct);
        if (!result.Success) return result;

        var newMail = await db.Mails.FindAsync([result.MailId!.Value], ct);
        if (newMail is not null && original.Attachments.Count > 0)
        {
            foreach (var a in original.Attachments)
            {
                db.MailAttachments.Add(new MailAttachment
                {
                    Id = Guid.NewGuid(), MailId = newMail.Id,
                    FileName = a.FileName, FileSize = a.FileSize, MimeType = a.MimeType,
                    SynologyServer = a.SynologyServer, SynologyPath = a.SynologyPath,
                    ThumbnailPath = a.ThumbnailPath, CreatedAt = DateTime.UtcNow,
                });
            }
            newMail.HasAttachment = true;
            await db.SaveChangesAsync(ct);
        }
        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Folder queries
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<PagedResult<MailSummaryDto>> GetFolderAsync(Guid userId, MailFolder folder, int page, int pageSize, MailSearchRequest? search = null, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        IQueryable<MailRecipient> query = db.MailRecipients.AsNoTracking().Where(r => r.UserId == userId);

        if (folder == MailFolder.Starred)
            query = query.Where(r => r.IsStarred);
        else
            query = query.Where(r => r.Folder == FolderToString(folder));

        if (search is not null)
        {
            // The subject/body/sender text match uses EF.Functions.ILike — Npgsql-only (Postgres).
            // It throws at translation on SQLite, the repo's TestDb seam, so the ILIKE branch is
            // not unit-testable there (same documented limitation as DirectoryEndpoints). The
            // star/unread/sender filters below are plain comparisons and ARE covered by tests.
            if (!string.IsNullOrWhiteSpace(search.Query))
            {
                var pattern = EscapeLikePattern(search.Query);
                // Use LIKE rather than ILIKE so the SQLite test/provider seam and Postgres both
                // translate the same query. SQLite LIKE is case-insensitive for ASCII by default.
                query = query.Where(r =>
                    EF.Functions.Like(r.Mail.Subject, pattern) ||
                    EF.Functions.Like(r.Mail.Body, pattern) ||
                    EF.Functions.Like(r.Mail.Sender.Name, pattern) ||
                    EF.Functions.Like(r.Mail.Sender.Nik, pattern));
            }
            if (search.StarredOnly == true) query = query.Where(r => r.IsStarred);
            if (search.UnreadOnly == true) query = query.Where(r => !r.IsRead);
            if (!string.IsNullOrWhiteSpace(search.SenderNik)) query = query.Where(r => r.Mail.Sender.Nik == search.SenderNik);
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .Include(r => r.Mail).ThenInclude(m => m.Sender)
            .Include(r => r.Mail).ThenInclude(m => m.Attachments)
            .OrderByDescending(r => r.IsPinned)
            .ThenByDescending(r => r.Mail.SentAt ?? r.Mail.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = items.Select(ToSummary).ToList();
        return new PagedResult<MailSummaryDto>(dtos, totalCount, page, pageSize);
    }

    public async Task<FolderCountsDto> GetFolderCountsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.MailRecipients
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .GroupBy(r => new { r.Folder, r.IsRead, r.IsStarred })
            .Select(g => new { g.Key.Folder, g.Key.IsRead, g.Key.IsStarred, Count = g.Count() })
            .ToListAsync(ct);

        int Get(string folder) => rows.Where(r => r.Folder == folder).Sum(r => r.Count);
        int GetUnread(string folder) => rows.Where(r => r.Folder == folder && !r.IsRead).Sum(r => r.Count);

        return new FolderCountsDto(
            GetUnread("inbox"), Get("inbox"),
            Get("sent"), Get("draft"), rows.Where(r => r.IsStarred).Sum(r => r.Count),
            Get("archive"), Get("trash"), Get("scheduled"));
    }

    public async Task<PagedResult<MailSummaryDto>> GetThreadAsync(Guid userId, Guid threadRootId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 100) pageSize = 100;

        var involvedMailIds = db.MailRecipients.Where(r => r.UserId == userId).Select(r => r.MailId);

        // Include the root message itself (ThreadRootId == null when it's the thread root) plus all replies.
        var query = db.Mails
            .AsNoTracking()
            .Where(m => (m.Id == threadRootId || m.ThreadRootId == threadRootId) && involvedMailIds.Contains(m.Id));

        var totalCount = await query.CountAsync(ct);

        var mails = await query
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .OrderBy(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = mails.Select(m => ToSummaryFromMail(m)).ToList();
        return new PagedResult<MailSummaryDto>(dtos, totalCount, page, pageSize);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Reading
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<MailDetailDto?> GetAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await db.MailRecipients
            .AsNoTracking()
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId, ct);
        if (recipient is null) return null; // hidden from non-participants (BCC stays private)

        var mail = await db.Mails
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Recipients).ThenInclude(r => r.User)
            .Include(m => m.Attachments)
            .FirstAsync(m => m.Id == mailId, ct);

        // BCC privacy (spec): a BCC recipient is visible only to the sender and to themselves.
        var callerIsSender = mail.SenderId == userId;
        var visibleRecipients = mail.Recipients
            .Where(r => r.RecipientType != "bcc" || callerIsSender || r.UserId == userId)
            .ToList();
        var visibleDelivery = mail.Recipients
            .Where(r => r.RecipientType != "from" && r.RecipientType != "bcc" && (callerIsSender || r.RecipientType is "to" or "cc" || r.UserId == userId))
            .ToList();

        return new MailDetailDto(
            mail.Id, mail.SenderId, mail.Sender.Name, mail.Sender.Nik,
            mail.Subject, mail.Body, mail.IsHtml, mail.Signature, mail.SentAt,
            mail.InReplyToId, mail.ThreadRootId, mail.ThreadCount,
            recipient.IsStarred, recipient.IsRead, recipient.Folder,
            mail.PdfExportRequestedBy, mail.PdfExportRequestedAt,
            visibleRecipients
                .OrderBy(r => r.RecipientType == "to" ? 0 : r.RecipientType == "cc" ? 1 : r.RecipientType == "bcc" ? 2 : 3)
                .Select(r => new MailRecipientDto(r.UserId, r.User.Name, r.User.Nik, r.User.Email, r.RecipientType))
                .ToList(),
            mail.Attachments.Select(a => new AttachmentDto(a.Id, a.FileName, a.FileSize, a.MimeType, a.ThumbnailPath)).ToList(),
            visibleDelivery
                .Select(r => new DeliveryReadStateDto(r.UserId, r.User.Name, r.User.Nik, r.RecipientType, r.DeliveredAt, r.ReadAt))
                .ToList(),
            recipient.IsPinned,
            mail.Importance ?? "normal",
            mail.ScheduledAt)
        {
            LinkedProjects = await ExtractLinkedProjectsAsync(mail.Body, userId, ct)
        };
    }

    public async Task<IReadOnlyList<DeliveryReadStateDto>> GetDeliveryStateAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var mail = await db.Mails.AsNoTracking()
            .Where(m => m.Id == mailId && m.SenderId == userId)
            .Include(m => m.Recipients).ThenInclude(r => r.User)
            .FirstOrDefaultAsync(ct);
        if (mail is null) return [];

        return mail.Recipients
            .Where(r => r.RecipientType != "from")
            .Select(r => new DeliveryReadStateDto(r.UserId, r.User.Name, r.User.Nik, r.RecipientType, r.DeliveredAt, r.ReadAt))
            .ToList();
    }

    public async Task RecordDeliveredAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await db.MailRecipients
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId, ct);
        if (recipient is null || recipient.DeliveredAt is not null) return;
        recipient.DeliveredAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Per-user folder actions — single-item
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<SendMailResult> MarkReadAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await FindRecipientAsync(userId, mailId, ct);
        if (recipient is null) return new SendMailResult(false, null, "mail_not_found");
        if (!recipient.IsRead)
        {
            recipient.IsRead = true;
            recipient.ReadAt = recipient.ReadAt ?? DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> MarkUnreadAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await FindRecipientAsync(userId, mailId, ct);
        if (recipient is null) return new SendMailResult(false, null, "mail_not_found");
        if (recipient.IsRead)
        {
            recipient.IsRead = false;
            recipient.ReadAt = null;
            await db.SaveChangesAsync(ct);
        }
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> ToggleStarAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await FindRecipientAsync(userId, mailId, ct);
        if (recipient is null) return new SendMailResult(false, null, "mail_not_found");
        recipient.IsStarred = !recipient.IsStarred;
        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> MoveToFolderAsync(Guid userId, Guid mailId, MailFolder folder, CancellationToken ct)
    {
        var recipient = await FindRecipientAsync(userId, mailId, ct);
        if (recipient is null) return new SendMailResult(false, null, "mail_not_found");
        if (folder == MailFolder.Starred) return new SendMailResult(false, null, "use_starred_flag");

        if (recipient.Folder == "trash" && FolderToString(folder) != "trash")
            return new SendMailResult(false, null, "mail_trashed");

        var folderStr = FolderToString(folder);
        if (recipient.Folder == folderStr) return new SendMailResult(true, mailId, null);
        recipient.Folder = folderStr;
        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mailId, null);
    }

    /// Soft delete: moves to Trash. In the Trash folder it becomes a hard delete of the user's row
    /// (the mail stays for other recipients — folders are per (mail, user)).
    public async Task<SendMailResult> DeleteAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await FindRecipientAsync(userId, mailId, ct);
        if (recipient is null) return new SendMailResult(false, null, "mail_not_found");

        if (recipient.Folder == "trash")
        {
            db.MailRecipients.Remove(recipient);
            await db.SaveChangesAsync(ct);
            return new SendMailResult(true, mailId, null);
        }

        recipient.Folder = "trash";
        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mailId, null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Per-user folder actions — bulk
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<BulkResultDto> BulkMoveAsync(Guid userId, IReadOnlyList<Guid> mailIds, MailFolder folder, CancellationToken ct)
    {
        if (mailIds.Count == 0) return new BulkResultDto(0);
        if (folder == MailFolder.Starred) return new BulkResultDto(0, "use_starred_flag");
        var folderStr = FolderToString(folder);

        var rows = await db.MailRecipients
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId) && r.Folder != "trash")
            .ToListAsync(ct);
        foreach (var r in rows) r.Folder = folderStr;
        await db.SaveChangesAsync(ct);
        return new BulkResultDto(rows.Count);
    }

    public async Task<BulkResultDto> BulkToggleStarAsync(Guid userId, IReadOnlyList<Guid> mailIds, CancellationToken ct)
    {
        if (mailIds.Count == 0) return new BulkResultDto(0);
        var rows = await db.MailRecipients
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId))
            .ToListAsync(ct);
        if (rows.Count == 0) return new BulkResultDto(0);
        var unstarred = rows.Where(r => !r.IsStarred).ToList();
        if (unstarred.Count > 0)
            foreach (var r in unstarred) r.IsStarred = true;
        else
            foreach (var r in rows) r.IsStarred = false;
        await db.SaveChangesAsync(ct);
        return new BulkResultDto(rows.Count);
    }

    public async Task<BulkResultDto> BulkDeleteAsync(Guid userId, IReadOnlyList<Guid> mailIds, CancellationToken ct)
    {
        if (mailIds.Count == 0) return new BulkResultDto(0);
        var rows = await db.MailRecipients
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId))
            .ToListAsync(ct);
        if (rows.Count == 0) return new BulkResultDto(0);

        var permanent = rows.Where(r => r.Folder == "trash").ToList();
        foreach (var r in rows.Except(permanent)) r.Folder = "trash";
        foreach (var r in permanent) db.MailRecipients.Remove(r);
        await db.SaveChangesAsync(ct);
        return new BulkResultDto(rows.Count);
    }

    public async Task<BulkResultDto> BulkRestoreAsync(Guid userId, IReadOnlyList<Guid> mailIds, CancellationToken ct)
    {
        if (mailIds.Count == 0) return new BulkResultDto(0);

        var rows = await db.MailRecipients
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId) && r.Folder == "trash")
            .ToListAsync(ct);
        foreach (var row in rows) row.Folder = "inbox";
        await db.SaveChangesAsync(ct);
        return new BulkResultDto(rows.Count);
    }

    public async Task<BulkResultDto> MarkAllReadAsync(Guid userId, MailFolder folder, CancellationToken ct)
    {
        if (folder == MailFolder.Starred) return new BulkResultDto(0, "use_starred_flag");
        var folderStr = FolderToString(folder);
        var now = DateTime.UtcNow;
        var rows = await db.MailRecipients
            .Where(r => r.UserId == userId && r.Folder == folderStr && !r.IsRead)
            .ToListAsync(ct);
        foreach (var r in rows) { r.IsRead = true; r.ReadAt = r.ReadAt ?? now; }
        await db.SaveChangesAsync(ct);
        return new BulkResultDto(rows.Count);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Signature/paraf + export stubs
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<SendMailResult> RequestPdfExportAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var isParticipant = await db.MailRecipients
            .AnyAsync(r => r.MailId == mailId && r.UserId == userId, ct);
        if (!isParticipant) return new SendMailResult(false, null, "mail_not_found");

        var mail = await db.Mails.FindAsync([mailId], ct);
        if (mail is null) return new SendMailResult(false, null, "mail_not_found");

        // PDF export stub: records the request only — real rendering lands with Wave-2.
        mail.PdfExportRequestedBy = userId.ToString();
        mail.PdfExportRequestedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mailId, null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Scheduling & Pinning
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<SendMailResult> CancelScheduleAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var senderRecipient = await db.MailRecipients
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder == "scheduled", ct);
        if (senderRecipient is null) return new SendMailResult(false, null, "mail_not_found");

        var mail = await db.Mails.Include(m => m.Recipients).FirstOrDefaultAsync(m => m.Id == mailId, ct);
        if (mail is null) return new SendMailResult(false, null, "mail_not_found");

        mail.Status = "draft";
        mail.ScheduledAt = null;
        mail.CancelledAt = DateTime.UtcNow;
        mail.UpdatedAt = DateTime.UtcNow;

        senderRecipient.Folder = "draft";

        var pendingTargets = mail.Recipients.Where(r => r.UserId != userId && r.Folder == "pending").ToList();
        foreach (var p in pendingTargets)
        {
            db.MailRecipients.Remove(p);
        }

        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> RescheduleAsync(Guid userId, Guid mailId, DateTime scheduledAt, CancellationToken ct)
    {
        var senderRecipient = await db.MailRecipients
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder == "scheduled", ct);
        if (senderRecipient is null) return new SendMailResult(false, null, "mail_not_found");

        var mail = await db.Mails.FindAsync([mailId], ct);
        if (mail is null) return new SendMailResult(false, null, "mail_not_found");

        mail.ScheduledAt = scheduledAt;
        mail.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> SendScheduledImmediatelyAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var senderRecipient = await db.MailRecipients
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder == "scheduled", ct);
        if (senderRecipient is null) return new SendMailResult(false, null, "mail_not_found");

        var mail = await db.Mails.Include(m => m.Recipients).FirstOrDefaultAsync(m => m.Id == mailId, ct);
        if (mail is null) return new SendMailResult(false, null, "mail_not_found");

        var now = DateTime.UtcNow;
        mail.Status = "sent";
        mail.SentAt = now;
        mail.ScheduledAt = now;
        mail.UpdatedAt = now;
        await BumpThreadAsync(mail, now, ct);

        senderRecipient.Folder = "sent";
        foreach (var r in mail.Recipients.Where(r => r.Folder == "pending"))
        {
            r.Folder = "inbox";
            r.DeliveredAt = now;
        }

        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> TogglePinAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await FindRecipientAsync(userId, mailId, ct);
        if (recipient is null) return new SendMailResult(false, null, "mail_not_found");

        recipient.IsPinned = !recipient.IsPinned;
        await db.SaveChangesAsync(ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<int> DeliverDueScheduledMailsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var dueMails = await db.Mails
            .Where(m => m.Status == "scheduled" && m.ScheduledAt != null && m.ScheduledAt <= now)
            .Include(m => m.Recipients)
            .ToListAsync(ct);

        if (dueMails.Count == 0) return 0;

        foreach (var mail in dueMails)
        {
            mail.Status = "sent";
            mail.SentAt = now;
            await BumpThreadAsync(mail, now, ct);

            foreach (var r in mail.Recipients)
            {
                if (r.RecipientType == "from" && r.Folder == "scheduled")
                {
                    r.Folder = "sent";
                }
                else if (r.Folder == "pending")
                {
                    r.Folder = "inbox";
                    r.DeliveredAt = now;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return dueMails.Count;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    private async Task<MailRecipient?> FindRecipientAsync(Guid userId, Guid mailId, CancellationToken ct)
        => await db.MailRecipients.FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId, ct);

    private async Task<IReadOnlyList<ProjectLink>> ExtractLinkedProjectsAsync(string body, Guid userId, CancellationToken ct)
    {
        var projectIds = System.Text.RegularExpressions.Regex.Matches(body ?? "", @"\(p:([0-9a-fA-F-]{36})\)")
            .Select(m => Guid.TryParse(m.Groups[1].Value, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (projectIds.Count == 0) return [];

        return await db.Projects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id) && p.IsActive &&
                db.ProjectMembers.Any(m => m.ProjectId == p.Id && m.UserId == userId))
            .OrderBy(p => p.Name)
            .Select(p => new ProjectLink(p.Id, p.Name, p.Progress, p.Status))
            .ToListAsync(ct);
    }

    /// JSON clients may omit cc/bcc/subject/body entirely — System.Text.Json leaves reference types
    /// null rather than empty. Normalize so every consumer can .Count/.Concat/.Trim safely.
    private static ComposeMailRequest Normalize(ComposeMailRequest req)
        => req with
        {
            Subject = req.Subject ?? "",
            Body = req.Body ?? "",
            To = req.To ?? [],
            Cc = req.Cc ?? [],
            Bcc = req.Bcc ?? [],
        };

    private async Task<IReadOnlyList<User>?> ResolveRecipientsAsync(ComposeMailRequest req, CancellationToken ct)
    {
        var niks = req.To.Concat(req.Cc).Concat(req.Bcc).Distinct().ToList();
        if (niks.Count == 0) return [];
        var users = await db.Users.AsNoTracking()
            .Where(u => niks.Contains(u.Nik) && u.IsActive)
            .ToListAsync(ct);
        // Every requested NIK must resolve to an active user — no silent drops.
        return users.Count == niks.Count ? users : null;
    }

    private async Task<bool> LinkAttachmentsAsync(Guid mailId, IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        if (ids is not { Count: > 0 }) return false;
        var orphans = await db.MailAttachments
            .Where(a => ids.Contains(a.Id) && a.MailId == Guid.Empty)
            .ToListAsync(ct);
        foreach (var a in orphans) a.MailId = mailId;
        return orphans.Count > 0;
    }

    /// Reply/forward threading: count + last-activity live on the thread root so the inbox list
    /// shows "n replies" and sorts by conversation recency without a join.
    private async Task BumpThreadAsync(MailEntity mail, DateTime now, CancellationToken ct)
    {
        if (mail.InReplyToId is null) return;
        var parent = await db.Mails.FindAsync([mail.InReplyToId.Value], ct);
        if (parent is null) return;

        // Always telescope through the parent so replies to replies remain in the
        // original conversation instead of creating a nested, undiscoverable root.
        mail.ThreadRootId = parent.ThreadRootId ?? parent.Id;
        var root = await db.Mails.FindAsync([mail.ThreadRootId.Value], ct);
        if (root is not null)
        {
            root.ThreadCount = await db.Mails.CountAsync(m => m.ThreadRootId == root.Id, ct) + 1;
            root.LastThreadAt = now;
        }
    }

    private static MailRecipient MakeRecipient(Guid mailId, Guid userId, string type, string folder, DateTime now) => new()
    {
        Id = Guid.NewGuid(), MailId = mailId, UserId = userId,
        RecipientType = type, Folder = folder, CreatedAt = now,
    };

    private static MailSummaryDto ToSummary(MailRecipient r)
        => ToSummaryFromMail(r.Mail, r.IsStarred, r.IsRead, r.IsPinned);

    private static MailSummaryDto ToSummaryFromMail(MailEntity m, bool starred = false, bool read = false, bool pinned = false) => new(
        m.Id, m.SenderId, m.Sender?.Name ?? "Unknown", m.Sender?.Nik ?? "Unknown",
        m.Subject, Truncate(m.Body, 120), m.SentAt ?? m.CreatedAt, read, starred,
        m.HasAttachment, m.ThreadCount, m.ThreadRootId is not null || m.InReplyToId is not null,
        m.ThreadRootId, pinned, m.Importance ?? "normal", m.ScheduledAt);

    private static SendMailResult? Validate(ComposeMailRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Subject)) return new SendMailResult(false, null, "subject_required");
        if (req.Subject.Trim().Length > MaxSubjectLength) return new SendMailResult(false, null, "subject_too_long");
        if (string.IsNullOrWhiteSpace(req.Body)) return new SendMailResult(false, null, "body_required");
        if (req.To.Count + req.Cc.Count + req.Bcc.Count == 0) return new SendMailResult(false, null, "no_recipients");
        if (req.To.Count + req.Cc.Count + req.Bcc.Count > MaxRecipients) return new SendMailResult(false, null, "too_many_recipients");
        return null;
    }

    private static SendMailResult? ValidateDraft(ComposeMailRequest req)
    {
        // Drafts are lenient: subject/body/recipients are all optional (auto-save).
        if (req.Subject.Length > MaxSubjectLength) return new SendMailResult(false, null, "subject_too_long");
        if (req.To.Count + req.Cc.Count + req.Bcc.Count > MaxRecipients) return new SendMailResult(false, null, "too_many_recipients");
        return null;
    }

    private static string FolderToString(MailFolder folder) => folder switch
    {
        MailFolder.Inbox => "inbox",
        MailFolder.Sent => "sent",
        MailFolder.Draft => "draft",
        MailFolder.Archive => "archive",
        MailFolder.Trash => "trash",
        MailFolder.Scheduled => "scheduled",
        _ => "inbox",
    };

    private static string EscapeLikePattern(string query)
        => $"%{query.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..maxLength] + "...";
    }
}
