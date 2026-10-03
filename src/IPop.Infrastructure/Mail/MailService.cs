using IPop.Application.Mail;
using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Alias: our own namespace IPop.Infrastructure.Mail shadows the Mail entity type in name lookup.
// Use a distinct alias name (MailEntity) so simple-name resolution inside this namespace
// does not pick the namespace over the entity type.
using MailEntity = IPop.Core.Entities.Mail;

namespace IPop.Infrastructure.Mail;

public sealed class MailService(IPopDbContext db, INotificationGatewayService? notificationGateway = null, IRealtime? realtime = null,
    MailNotifySignal? notifySignal = null, ILogger<MailService>? log = null) : IMailService
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    private const int MaxSubjectLength = 500;
    private ILogger Log => log ?? NullLogger<MailService>.Instance;

    // ──────────────────────────────────────────────────────────────────────────
    // Compose & delivery
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<SendMailResult> SendAsync(Guid senderId, ComposeMailRequest req, CancellationToken ct)
    {
        req = Normalize(req);
        var validation = Validate(req);
        if (validation is not null) return validation;

        var x = await ExpandAsync(senderId, req, ct);
        if (x.Invalid.Count > 0)
            return new SendMailResult(false, null, x.Invalid.Any(i => i.StartsWith("group:")) ? "group_not_found" : "recipient_not_found");
        // Sending to yourself: the sender's own row (Folder=sent) IS the record — a second (mail,
        // user) row would trip the unique index, so the sender never gets a recipient row.
        var recipients = x.Recipients.Where(r => r.User.Id != senderId).ToList();
        if (x.Recipients.Count == 0) return new SendMailResult(false, null, "no_recipients");
        var total = recipients.Count;
        if (total > MailLimits.MaxRecipients) return new SendMailResult(false, null, "too_many_recipients", total);
        // The sender must have seen (and confirmed) the server's own count for a big send.
        if (total > MailLimits.ConfirmAbove && req.ConfirmedRecipientCount != total)
            return new SendMailResult(false, null, "confirm_required", total);

        // Mass mail: everyone but the original sender may only reply to the sender.
        if (req.InReplyToId is Guid parentId)
        {
            var parent = await db.Mails.AsNoTracking().Where(m => m.Id == parentId)
                .Select(m => new { m.SenderId, m.IsMass, m.HideRecipients }).FirstOrDefaultAsync(ct);
            if (parent is not null && (parent.IsMass || parent.HideRecipients) && parent.SenderId != senderId
                && (x.Groups.Count > 0 || recipients.Any(r => r.User.Id != parent.SenderId)))
                return new SendMailResult(false, null, "reply_all_blocked");
        }

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
            ScheduledAt = req.ScheduledAt is { } schedAt ? ToUtc(schedAt) : null,
            CreatedAt = now,
            SentAt = isScheduled ? null : now,
            IsMass = total > MailLimits.ConfirmAbove || x.Groups.Count > 0,
            HideRecipients = req.HideRecipients,
            RecipientCount = total,
            RecipientGroupsJson = x.Groups.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(x.Groups, Json) : null,
            SkippedJson = x.Skipped.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(x.Skipped, Json) : null,
        };
        if (!isScheduled) await BumpThreadAsync(mail, now, ct);
        db.Mails.Add(mail);

        // Sender's own row lives in Scheduled or Sent.
        db.MailRecipients.Add(MakeRecipient(mail.Id, senderId, "from", isScheduled ? "scheduled" : "sent", now));

        foreach (var r in recipients)
        {
            var row = MakeRecipient(mail.Id, r.User.Id, r.Type, isScheduled ? "pending" : "inbox", now);
            row.ViaGroup = r.ViaGroup;
            row.DeliveredAt = isScheduled ? null : now;
            db.MailRecipients.Add(row);
        }

        mail.HasAttachment = await LinkAttachmentsAsync(mail.Id, senderId, req.AttachmentIds, ct);
        await ApplySenderLabelsAsync(mail.Id, senderId, req.LabelIds, now, ct);
        // Badges/banners/push go through a durable queue written with the inbox rows: the send
        // returns as soon as the mail is in everyone's inbox, and no notification is lost.
        if (!isScheduled) QueueNotify(mail.Id, now);
        await db.SaveChangesAsync(ct);

        var recipientUsers = recipients.Select(r => r.User).ToList();
        // Fase 3 — aturan label otomatis: untuk tiap penerima non-pengirim, tempel label
        // dari aturan aktif yang cocok dengan pengirim (nama/NIK/email).
        if (!isScheduled)
            await ApplyAutoRulesAsync(mail, recipientUsers, ct);

        // Fase 3 — balasan otomatis (OOO): tiap penerima yang sedang OOO membalas sekali
        // per hari per pengirim. Not for mass mail: a broadcast would flood the sender.
        if (!isScheduled && !mail.IsMass)
            await SendAutoRepliesAsync(mail, recipientUsers, ct);

        await SyncUserAsync(senderId, ct);
        if (!isScheduled) notifySignal?.Notify();

        return new SendMailResult(true, mail.Id, null, total);
    }

    public async Task<RecipientPreviewDto> PreviewRecipientsAsync(Guid senderId, ComposeMailRequest req, CancellationToken ct)
    {
        req = NormalizeRecipients(req);
        var x = await ExpandAsync(senderId, req, ct);
        var total = x.Recipients.Count(r => r.User.Id != senderId);
        return new RecipientPreviewDto(total, x.Individuals, x.Duplicates, MailLimits.MaxRecipients,
            total > MailLimits.ConfirmAbove, x.Groups, x.Skipped, x.Invalid);
    }

    private sealed record Expanded(User User, string Type, bool ViaGroup);
    private sealed record Expansion(
        List<Expanded> Recipients, List<RecipientGroupCountDto> Groups, List<SkippedRecipientDto> Skipped,
        List<string> Invalid, int Individuals, int Duplicates);

    private static int TypeRank(string type) => type switch { "to" => 0, "cc" => 1, _ => 2 };
    private static string NormalizeField(string? field) => field?.Trim().ToLowerInvariant() switch { "cc" => "cc", "bcc" => "bcc", _ => "to" };
    private static string NormalizeKind(string? kind) => string.Equals(kind?.Trim(), "personal", StringComparison.OrdinalIgnoreCase) ? "personal" : "shared";

    /// Typed NIKs + group chips → one entry per person. Someone reached twice keeps the most
    /// visible field (To over Cc over Bcc) and counts as typed if they were typed anywhere.
    /// Typed NIKs that don't resolve to an active user are invalid; inactive group members are
    /// skipped and reported instead (groups go stale, typed names don't).
    private async Task<Expansion> ExpandAsync(Guid senderId, ComposeMailRequest req, CancellationToken ct)
    {
        var map = new Dictionary<Guid, Expanded>();
        var order = new List<Guid>();
        var entries = 0;
        void Add(User u, string type, bool viaGroup)
        {
            entries++;
            if (map.TryGetValue(u.Id, out var cur))
                map[u.Id] = cur with { Type = TypeRank(type) < TypeRank(cur.Type) ? type : cur.Type, ViaGroup = cur.ViaGroup && viaGroup };
            else
            {
                map[u.Id] = new Expanded(u, type, viaGroup);
                order.Add(u.Id);
            }
        }

        var typed = req.To.Select(n => (Nik: n, Type: "to"))
            .Concat(req.Cc.Select(n => (Nik: n, Type: "cc")))
            .Concat(req.Bcc.Select(n => (Nik: n, Type: "bcc")))
            .ToList();
        var niks = typed.Select(t => t.Nik).Distinct().ToList();
        var users = niks.Count == 0
            ? new Dictionary<string, User>()
            : (await db.Users.AsNoTracking().Where(u => niks.Contains(u.Nik) && u.IsActive).ToListAsync(ct))
                .GroupBy(u => u.Nik).ToDictionary(g => g.Key, g => g.First());
        var invalid = niks.Where(n => !users.ContainsKey(n)).ToList();
        foreach (var (nik, type) in typed)
            if (users.TryGetValue(nik, out var u)) Add(u, type, false);
        var individuals = map.Count;

        var groups = new List<RecipientGroupCountDto>();
        var skipped = new Dictionary<string, SkippedRecipientDto>();
        var seen = new HashSet<(string, Guid)>();
        foreach (var g in req.Groups ?? [])
        {
            var kind = NormalizeKind(g.Kind);
            if (!seen.Add((kind, g.Id))) continue;
            var field = NormalizeField(g.Field);
            var found = kind == "personal"
                ? await db.MailGroups.AsNoTracking().Where(x => x.Id == g.Id && x.OwnerId == senderId)
                    .Select(x => new { x.Name, Users = x.Members.Select(m => m.User).ToList() }).FirstOrDefaultAsync(ct)
                : await db.DirGroups.AsNoTracking().Where(x => x.Id == g.Id && x.IsActive)
                    .Select(x => new { x.Name, Users = x.Members.Select(m => m.User).ToList() }).FirstOrDefaultAsync(ct);
            if (found is null) { invalid.Add($"group:{g.Id}"); continue; }
            var active = found.Users.Where(u => u.IsActive).ToList();
            foreach (var u in found.Users.Where(u => !u.IsActive)) skipped.TryAdd(u.Nik, new SkippedRecipientDto(u.Nik, u.Name));
            foreach (var u in active) Add(u, field, true);
            groups.Add(new RecipientGroupCountDto(field, kind, g.Id, found.Name, active.Count));
        }

        return new Expansion(order.Select(id => map[id]).ToList(), groups, skipped.Values.OrderBy(v => v.Name).ToList(),
            invalid, individuals, entries - map.Count);
    }

    private void QueueNotify(Guid mailId, DateTime now)
        => db.MailNotifyJobs.Add(new MailNotifyJob { Id = Guid.NewGuid(), MailId = mailId, CreatedAt = now, NextAttemptAt = now });

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
            DraftState = SerializeDraftState(req),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Mails.Add(mail);
        // Drafts only have the author's row — recipients stay NIKs in DraftState until sent.
        db.MailRecipients.Add(MakeRecipient(mail.Id, userId, "from", "draft", now));
        mail.HasAttachment = await LinkAttachmentsAsync(mail.Id, userId, req.AttachmentIds, ct);
        await ApplySenderLabelsAsync(mail.Id, userId, req.LabelIds, now, ct);
        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
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
        mail.DraftState = SerializeDraftState(req);
        mail.UpdatedAt = DateTime.UtcNow;
        mail.HasAttachment = await LinkAttachmentsAsync(mail.Id, userId, req.AttachmentIds, ct, syncDraft: true);
        // The composer sends its full label set every save: replace the author's labels.
        db.MailLabelAssignments.RemoveRange(await db.MailLabelAssignments
            .Where(a => a.MailId == mailId && a.Label.UserId == userId).ToListAsync(ct));
        await ApplySenderLabelsAsync(mail.Id, userId, req.LabelIds, mail.UpdatedAt.Value, ct);
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
        await SyncUserAsync(userId, ct);
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
        var carried = req.ForwardAttachmentIds is null
            ? original.Attachments.ToList()
            : original.Attachments.Where(a => req.ForwardAttachmentIds.Contains(a.Id)).ToList();
        if (newMail is not null && carried.Count > 0)
        {
            foreach (var a in carried)
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
            // Filter label (Fase 3): surat yang ditempel label bernama ini milik pengguna.
            if (!string.IsNullOrWhiteSpace(search.Label))
            {
                var labelName = search.Label.Trim();
                query = query.Where(r => db.MailLabelAssignments
                    .Any(a => a.MailId == r.MailId && a.Label.UserId == userId && a.Label.Name == labelName));
            }
            // Rentang tanggal kirim (Surat Lanjutan).
            if (search.DateFrom is { } from)
                query = query.Where(r => (r.Mail.SentAt ?? r.Mail.CreatedAt) >= from);
            if (search.DateTo is { } to)
                query = query.Where(r => (r.Mail.SentAt ?? r.Mail.CreatedAt) <= to.AddDays(1));
            // Punya lampiran (Surat Lanjutan).
            if (search.HasAttachment == true)
                query = query.Where(r => r.Mail.HasAttachment);
            if (!string.IsNullOrWhiteSpace(search.Importance))
                query = query.Where(r => r.Mail.Importance == search.Importance.ToLowerInvariant().Trim());
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

        // Label per surat pada halaman ini (satu query untuk seluruh halaman).
        var mailIds = items.Select(r => r.MailId).ToList();
        var labelByMail = mailIds.Count == 0
            ? new Dictionary<Guid, List<string>>()
            : (await db.MailLabelAssignments.AsNoTracking()
                .Where(a => mailIds.Contains(a.MailId) && a.Label.UserId == userId)
                .Select(a => new { a.MailId, a.Label.Name })
                .ToListAsync(ct))
                .GroupBy(x => x.MailId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToList());

        var dtos = items.Select(r => ToSummary(r) with
        {
            Labels = labelByMail.TryGetValue(r.MailId, out var lbls) ? lbls : null
        }).ToList();
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
            // Newest first so page 1 holds the latest messages; Id breaks CreatedAt ties so
            // paging is stable (no message skipped or repeated across pages).
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
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

        // Recipients are NOT included: a mass mail has up to 1000 of them. The reading pane gets
        // the first DetailRecipients names plus counts; the rest is paged on demand.
        var mail = await db.Mails
            .AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .FirstAsync(m => m.Id == mailId, ct);

        var callerIsSender = mail.SenderId == userId;
        var visible = VisibleRecipients(mail, userId);
        var recipientTotal = await visible.CountAsync(r => r.RecipientType != "from", ct);
        // Typed people first; group members are summarised by their group chip.
        var visibleRecipients = await visible
            .Where(r => !r.ViaGroup || r.UserId == userId || r.RecipientType == "from")
            .OrderBy(r => r.RecipientType == "to" ? 0 : r.RecipientType == "cc" ? 1 : r.RecipientType == "bcc" ? 2 : 3)
            .ThenBy(r => r.CreatedAt)
            .Take(MailLimits.DetailRecipients + 1)
            .Include(r => r.User)
            .ToListAsync(ct);
        // A draft's recipients live as NIKs in DraftState; show them to the author like real rows.
        var draftState = mail.Status == "draft" && callerIsSender ? DeserializeDraftState(mail.DraftState) : null;
        if (draftState is not null)
        {
            var drafted = await DraftRecipientsAsync(mail.Id, draftState, ct);
            visibleRecipients.AddRange(drafted);
            recipientTotal += drafted.Count;
        }
        // Per-recipient delivery state stays for ordinary mail; mass mail uses GetStatsAsync.
        var visibleDelivery = mail.IsMass
            ? []
            : visibleRecipients
                .Where(r => r.RecipientType != "from" && r.RecipientType != "bcc" && r.Folder != "draft" && (callerIsSender || r.RecipientType is "to" or "cc" || r.UserId == userId))
                .ToList();

        var groups = draftState is not null
            ? await ResolveGroupRefsAsync(userId, draftState.Groups ?? [], ct)
            : VisibleGroups(mail, callerIsSender);

        return new MailDetailDto(
            mail.Id, mail.SenderId, mail.Sender.Name, mail.Sender.Nik,
            mail.Subject, mail.IsHtml ? (draftState is not null || mail.Status == "draft" ? MailHtml.Sanitize(mail.Body) : MailHtml.ForDisplay(mail.Body)) : mail.Body, mail.IsHtml, mail.Signature, mail.SentAt,
            mail.InReplyToId, mail.ThreadRootId, mail.ThreadCount,
            recipient.IsStarred, recipient.IsRead, recipient.Folder,
            mail.PdfExportRequestedBy, mail.PdfExportRequestedAt,
            visibleRecipients
                .OrderBy(r => r.RecipientType == "to" ? 0 : r.RecipientType == "cc" ? 1 : r.RecipientType == "bcc" ? 2 : 3)
                // The author reopening a draft needs every typed recipient back in the composer.
                .Take(draftState is not null ? int.MaxValue : MailLimits.DetailRecipients)
                .Select(r => new MailRecipientDto(r.UserId, r.User.Name, r.User.Nik, r.User.Email, r.RecipientType))
                .ToList(),
            mail.Attachments.OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt)
                .Select(a => new AttachmentDto(a.Id, a.FileName, a.FileSize, a.MimeType, a.ThumbnailPath)).ToList(),
            visibleDelivery
                .Select(r => new DeliveryReadStateDto(r.UserId, r.User.Name, r.User.Nik, r.RecipientType, r.DeliveredAt, r.ReadAt))
                .ToList(),
            recipient.IsPinned,
            mail.Importance ?? "normal",
            mail.ScheduledAt,
            recipient.IsMuted,
            MailHtml.ToPlainText(mail.Body))
        {
            LinkedProjects = await ExtractLinkedProjectsAsync(mail.Body, userId, ct),
            LabelIds = await db.MailLabelAssignments.AsNoTracking()
                .Where(a => a.MailId == mailId && a.Label.UserId == userId)
                .Select(a => a.LabelId)
                .ToListAsync(ct),
            DraftExtra = draftState?.Extra,
            RecipientTotal = recipientTotal,
            RecipientGroups = groups,
            IsMass = mail.IsMass,
            HideRecipients = draftState?.HideRecipients ?? mail.HideRecipients,
            CanReplyAll = callerIsSender || !(mail.IsMass || mail.HideRecipients),
        };
    }

    /// Recipient rows the caller may see. BCC privacy (spec): a BCC recipient is visible only to
    /// the sender and to themselves; with "Hide recipient list" everyone else is BCC-like too.
    private IQueryable<MailRecipient> VisibleRecipients(MailEntity mail, Guid userId)
    {
        var q = db.MailRecipients.AsNoTracking().Where(r => r.MailId == mail.Id);
        if (mail.SenderId == userId) return q;
        if (mail.HideRecipients) return q.Where(r => r.UserId == userId || r.RecipientType == "from");
        return q.Where(r => r.RecipientType != "bcc" || r.UserId == userId);
    }

    private static List<RecipientGroupCountDto> VisibleGroups(MailEntity mail, bool callerIsSender)
    {
        if (string.IsNullOrEmpty(mail.RecipientGroupsJson)) return [];
        if (!callerIsSender && mail.HideRecipients) return [];
        List<RecipientGroupCountDto> all;
        try { all = System.Text.Json.JsonSerializer.Deserialize<List<RecipientGroupCountDto>>(mail.RecipientGroupsJson, Json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
        return callerIsSender
            ? all
            // Others see names and sizes only — no ids to enumerate a personal group with.
            : all.Where(g => g.Field != "bcc").Select(g => g with { Id = Guid.Empty }).ToList();
    }

    /// Group chips saved on a draft → names + current active member counts.
    private async Task<List<RecipientGroupCountDto>> ResolveGroupRefsAsync(Guid ownerId, IReadOnlyList<MailGroupRef> refs, CancellationToken ct)
    {
        var result = new List<RecipientGroupCountDto>();
        foreach (var g in refs)
        {
            var kind = NormalizeKind(g.Kind);
            var found = kind == "personal"
                ? await db.MailGroups.AsNoTracking().Where(x => x.Id == g.Id && x.OwnerId == ownerId)
                    .Select(x => new { x.Name, Count = x.Members.Count(m => m.User.IsActive) }).FirstOrDefaultAsync(ct)
                : await db.DirGroups.AsNoTracking().Where(x => x.Id == g.Id && x.IsActive)
                    .Select(x => new { x.Name, Count = x.Members.Count(m => m.User.IsActive) }).FirstOrDefaultAsync(ct);
            if (found is not null) result.Add(new RecipientGroupCountDto(NormalizeField(g.Field), kind, g.Id, found.Name, found.Count));
        }
        return result;
    }

    public async Task<MailStatsDto?> GetStatsAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var mail = await db.Mails.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mailId && m.SenderId == userId, ct);
        if (mail is null) return null;
        var rows = db.MailRecipients.AsNoTracking().Where(r => r.MailId == mailId && r.RecipientType != "from");
        var counts = await rows.GroupBy(_ => 1).Select(g => new
        {
            Sent = g.Count(),
            Delivered = g.Count(r => r.DeliveredAt != null),
            Read = g.Count(r => r.ReadAt != null),
        }).FirstOrDefaultAsync(ct);
        List<SkippedRecipientDto> skipped = [];
        if (!string.IsNullOrEmpty(mail.SkippedJson))
        {
            try { skipped = System.Text.Json.JsonSerializer.Deserialize<List<SkippedRecipientDto>>(mail.SkippedJson, Json) ?? []; }
            catch (System.Text.Json.JsonException) { }
        }
        return new MailStatsDto(counts?.Sent ?? 0, counts?.Delivered ?? 0, counts?.Read ?? 0, skipped);
    }

    public async Task<PagedResult<MailReaderDto>?> GetReadersAsync(Guid userId, Guid mailId, bool read, int page, int pageSize, CancellationToken ct)
    {
        if (!await db.Mails.AnyAsync(m => m.Id == mailId && m.SenderId == userId, ct)) return null;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.MailRecipients.AsNoTracking()
            .Where(r => r.MailId == mailId && r.RecipientType != "from" && (read ? r.ReadAt != null : r.ReadAt == null));
        var total = await q.CountAsync(ct);
        var ordered = read ? q.OrderByDescending(r => r.ReadAt).ThenBy(r => r.User.Name) : q.OrderBy(r => r.User.Name);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new MailReaderDto(r.User.Name, r.User.Nik, r.ReadAt))
            .ToListAsync(ct);
        return new PagedResult<MailReaderDto>(items, total, page, pageSize);
    }

    public async Task<PagedResult<MailRecipientDto>?> GetRecipientsAsync(Guid userId, Guid mailId, int page, int pageSize, CancellationToken ct)
    {
        var mail = await db.Mails.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mailId, ct);
        if (mail is null || !await db.MailRecipients.AnyAsync(r => r.MailId == mailId && r.UserId == userId, ct)) return null;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = VisibleRecipients(mail, userId).Where(r => r.RecipientType != "from");
        // Group members are listed only to the sender; others see the group chip.
        if (mail.SenderId != userId) q = q.Where(r => !r.ViaGroup || r.UserId == userId);
        var total = await q.CountAsync(ct);
        var items = await q
            .OrderBy(r => r.RecipientType == "to" ? 0 : r.RecipientType == "cc" ? 1 : 2).ThenBy(r => r.User.Name)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new MailRecipientDto(r.UserId, r.User.Name, r.User.Nik, r.User.Email, r.RecipientType))
            .ToListAsync(ct);
        return new PagedResult<MailRecipientDto>(items, total, page, pageSize);
    }

    private sealed record DraftStateJson(IReadOnlyList<string>? To, IReadOnlyList<string>? Cc, IReadOnlyList<string>? Bcc, string? Extra,
        IReadOnlyList<MailGroupRef>? Groups = null, bool HideRecipients = false);

    private static string? SerializeDraftState(ComposeMailRequest req)
        => req.To.Count + req.Cc.Count + req.Bcc.Count + (req.Groups?.Count ?? 0) == 0 && string.IsNullOrEmpty(req.DraftExtra) && !req.HideRecipients
            ? null
            : System.Text.Json.JsonSerializer.Serialize(new DraftStateJson(req.To, req.Cc, req.Bcc, req.DraftExtra,
                req.Groups is { Count: > 0 } ? req.Groups : null, req.HideRecipients));

    private static DraftStateJson? DeserializeDraftState(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<DraftStateJson>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// Unsaved recipient rows (never added to the context) so GetAsync can map them like real ones.
    private async Task<List<MailRecipient>> DraftRecipientsAsync(Guid mailId, DraftStateJson s, CancellationToken ct)
    {
        var typed = (s.To ?? []).Select(n => (n, "to"))
            .Concat((s.Cc ?? []).Select(n => (n, "cc")))
            .Concat((s.Bcc ?? []).Select(n => (n, "bcc")))
            .ToList();
        if (typed.Count == 0) return [];
        var niks = typed.Select(t => t.n).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => niks.Contains(u.Nik)).ToDictionaryAsync(u => u.Nik, ct);
        return typed.Where(t => users.ContainsKey(t.n))
            .Select(t => new MailRecipient { MailId = mailId, UserId = users[t.n].Id, User = users[t.n], RecipientType = t.Item2, Folder = "draft" })
            .ToList();
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
            await SyncUserAsync(userId, ct);
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
            await SyncUserAsync(userId, ct);
        }
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> ToggleStarAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await FindRecipientAsync(userId, mailId, ct);
        if (recipient is null) return new SendMailResult(false, null, "mail_not_found");
        recipient.IsStarred = !recipient.IsStarred;
        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
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
        await SyncUserAsync(userId, ct);
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
            await SyncUserAsync(userId, ct);
            return new SendMailResult(true, mailId, null);
        }

        recipient.Folder = "trash";
        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<int> EmptyTrashAsync(Guid userId, CancellationToken ct)
    {
        var trashed = await db.MailRecipients.Where(r => r.UserId == userId && r.Folder == "trash").ToListAsync(ct);
        var count = trashed.Count;
        if (count == 0) return 0;

        db.MailRecipients.RemoveRange(trashed);
        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
        return count;
    }

    public async Task<bool> ToggleMuteAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await db.MailRecipients.FirstOrDefaultAsync(r => r.UserId == userId && r.MailId == mailId, ct);
        if (recipient is null) return false;

        recipient.IsMuted = !recipient.IsMuted;
        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
        return recipient.IsMuted;
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
        await SyncUserAsync(userId, ct);
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
        await SyncUserAsync(userId, ct);
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
        await SyncUserAsync(userId, ct);
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
        await SyncUserAsync(userId, ct);
        return new BulkResultDto(rows.Count);
    }

    public async Task<BulkResultDto> BulkMarkReadAsync(Guid userId, IReadOnlyList<Guid> mailIds, CancellationToken ct)
    {
        if (mailIds.Count == 0) return new BulkResultDto(0);
        var now = DateTime.UtcNow;
        var rows = await db.MailRecipients
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId) && !r.IsRead)
            .ToListAsync(ct);
        foreach (var r in rows) { r.IsRead = true; r.ReadAt = r.ReadAt ?? now; }
        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
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
        await SyncUserAsync(userId, ct);
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

        // Catat permintaan ekspor (audit). Rendering sungguhan dilakukan endpoint via
        // MailPdfRenderer (DevExpress Document Processing) — bukan stub lagi.
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
        await SyncUserAsync(userId, ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> RescheduleAsync(Guid userId, Guid mailId, DateTime scheduledAt, CancellationToken ct)
    {
        var senderRecipient = await db.MailRecipients
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder == "scheduled", ct);
        if (senderRecipient is null) return new SendMailResult(false, null, "mail_not_found");

        var mail = await db.Mails.FindAsync([mailId], ct);
        if (mail is null) return new SendMailResult(false, null, "mail_not_found");

        if (mail.Status != "scheduled") return new SendMailResult(false, null, "mail_not_found");
        if (ScheduleError(scheduledAt) is { } bad) return new SendMailResult(false, null, bad);
        mail.ScheduledAt = ToUtc(scheduledAt);
        mail.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
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
        var delivered = mail.Recipients.Where(r => r.Folder == "pending").ToList();
        foreach (var r in delivered)
        {
            r.Folder = "inbox";
            r.DeliveredAt = now;
        }
        QueueNotify(mail.Id, now);

        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
        notifySignal?.Notify();
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> TogglePinAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await FindRecipientAsync(userId, mailId, ct);
        if (recipient is null) return new SendMailResult(false, null, "mail_not_found");

        recipient.IsPinned = !recipient.IsPinned;
        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<int> ProcessNotifyJobsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var jobs = await db.MailNotifyJobs
            .Where(j => j.DoneAt == null && j.NextAttemptAt <= now && j.Attempts < MailNotifyJob.MaxAttempts)
            .OrderBy(j => j.NextAttemptAt)
            .Take(10)
            .ToListAsync(ct);
        foreach (var job in jobs)
        {
            try
            {
                await NotifyDeliveredAsync(job, ct);
                job.DoneAt = DateTime.UtcNow;
                job.LastError = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                job.LastError = Truncate(ex.Message, 1000);
                if (job.Attempts + 1 >= MailNotifyJob.MaxAttempts)
                    Log.LogError(ex, "Mail {MailId} notification gave up after {Attempts} attempts", job.MailId, job.Attempts + 1);
                else
                    Log.LogWarning(ex, "Mail {MailId} notification attempt {Attempt} failed; retrying", job.MailId, job.Attempts + 1);
                // 10s, 20s, 40s … capped at 10 minutes.
                job.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Min(600, 10 * Math.Pow(2, job.Attempts)));
            }
            job.Attempts++;
            await db.SaveChangesAsync(ct);
        }
        return jobs.Count;
    }

    /// Badge + "mail.new" banner for everyone who just got the mail in their inbox. Shared by
    /// immediate send and scheduled delivery so both surface the same way. Realtime is a hint and
    /// runs once; the gateway (notification centre + push) is what a failed attempt retries.
    private async Task NotifyDeliveredAsync(MailNotifyJob job, CancellationToken ct)
    {
        var mail = await db.Mails.AsNoTracking().Include(m => m.Sender).FirstOrDefaultAsync(m => m.Id == job.MailId, ct);
        if (mail is null || mail.Status != "sent") return;
        var recipients = await db.MailRecipients.AsNoTracking()
            .Where(r => r.MailId == mail.Id && r.RecipientType != "from" && r.UserId != mail.SenderId && r.DeliveredAt != null)
            .Select(r => new { r.UserId, r.User.Nik })
            .ToListAsync(ct);
        if (recipients.Count == 0) return;

        if (realtime != null && job.Attempts == 0)
        {
            foreach (var chunk in recipients.Chunk(500))
            {
                var ids = chunk.Select(r => r.UserId).ToList();
                var unread = await db.MailRecipients.AsNoTracking()
                    .Where(r => ids.Contains(r.UserId) && r.Folder == "inbox" && !r.IsRead && !r.IsMuted)
                    .GroupBy(r => r.UserId)
                    .Select(g => new { UserId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);
                try
                {
                    foreach (var r in chunk)
                        await realtime.ToUserAsync(r.Nik, RealtimeEvents.BadgeChanged, new RealtimeArgs("mail", unread.GetValueOrDefault(r.UserId)), ct);
                    var niks = chunk.Select(r => r.Nik).ToList();
                    await realtime.ToUsersAsync(niks, "mailReceived", new { mailId = mail.Id, subject = mail.Subject }, ct);
                    await realtime.ToUsersAsync(niks, RealtimeEvents.MailChanged, ct: ct);
                }
                catch (Exception ex)
                {
                    // Realtime is a hint; clients re-fetch over REST on next load/reconnect.
                    Log.LogWarning(ex, "Mail {MailId} realtime hint failed for {Count} recipients", mail.Id, chunk.Length);
                }
            }
        }

        if (notificationGateway is null) return;
        var mutedUserIds = mail.ThreadRootId != null
            ? (await db.MailRecipients.AsNoTracking()
                .Where(r => (r.MailId == mail.ThreadRootId || r.Mail.ThreadRootId == mail.ThreadRootId) && r.IsMuted)
                .Select(r => r.UserId).ToListAsync(ct)).ToHashSet()
            : [];
        var recipientIds = recipients.Select(r => r.UserId).Where(id => !mutedUserIds.Contains(id)).Distinct().ToList();
        if (recipientIds.Count == 0) return;

        var preview = Truncate(MailHtml.ToPlainText(mail.Body), 120);
        var result = await notificationGateway.PublishInternalAsync(
            clientSlug: "mail",
            eventType: "mail.new",
            title: $"New email from {mail.Sender?.Name ?? "Someone"}",
            message: string.IsNullOrWhiteSpace(preview) ? mail.Subject : $"{mail.Subject} — {preview}",
            target: new NotificationTarget(UserIds: recipientIds),
            priority: mail.Importance == "high" ? "high" : "normal",
            targetUrl: $"/mail?mailId={mail.Id}",
            ct: ct);
        // A rejected request (validation) won't succeed on retry; only exceptions are retried.
        if (result is { Success: false }) job.LastError = Truncate($"{result.ErrorCode}: {result.Error}", 1000);
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
            // Scheduled mail used to land silently: no badge, no banner.
            QueueNotify(mail.Id, now);
        }

        await db.SaveChangesAsync(ct);
        foreach (var senderId in dueMails.Select(m => m.SenderId).Distinct())
            await SyncUserAsync(senderId, ct);
        notifySignal?.Notify();
        return dueMails.Count;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// MailChanged + a fresh mail badge to one user so their other tabs and nav badge resync
    /// after a per-user action. Best-effort: a hub failure never fails the request.
    private async Task SyncUserAsync(Guid userId, CancellationToken ct)
    {
        if (realtime is null) return;
        try
        {
            var nik = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(nik)) return;
            var unread = await db.MailRecipients.AsNoTracking()
                .CountAsync(r => r.UserId == userId && r.Folder == "inbox" && !r.IsRead && !r.IsMuted, ct);
            await realtime.ToUserAsync(nik, RealtimeEvents.BadgeChanged, new RealtimeArgs("mail", unread), ct);
            await realtime.ToUserAsync(nik, RealtimeEvents.MailChanged, ct: ct);
        }
        catch
        {
            // Realtime is a hint; clients re-fetch over REST on next load.
        }
    }

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
    /// Labels are per user, so only labels the sender owns are attached — to the sender's copy.
    private async Task ApplySenderLabelsAsync(Guid mailId, Guid senderId, IReadOnlyList<Guid>? labelIds, DateTime now, CancellationToken ct)
    {
        if (labelIds is null || labelIds.Count == 0) return;
        var ids = labelIds.Distinct().ToList();
        var owned = await db.MailLabels
            .Where(l => l.UserId == senderId && ids.Contains(l.Id))
            .Select(l => l.Id)
            .ToListAsync(ct);
        foreach (var id in owned)
            db.MailLabelAssignments.Add(new MailLabelAssignment { Id = Guid.NewGuid(), LabelId = id, MailId = mailId, CreatedAt = now });
    }

    private static ComposeMailRequest Normalize(ComposeMailRequest req)
        => req with
        {
            Subject = req.Subject ?? "",
            Body = MailHtml.Sanitize(req.Body),
            To = req.To ?? [],
            Cc = req.Cc ?? [],
            Bcc = req.Bcc ?? [],
            Groups = req.Groups ?? [],
        };

    private static ComposeMailRequest NormalizeRecipients(ComposeMailRequest req)
        => req with { To = req.To ?? [], Cc = req.Cc ?? [], Bcc = req.Bcc ?? [], Groups = req.Groups ?? [] };

    /// Attaches the composer's files to <paramref name="mailId"/>. A file can be claimed when it is
    /// a pending upload of this user (MailId null, UploaderId = user) or already sits on another of
    /// this user's drafts (autosave links uploads to the draft; sending then moves them to the sent
    /// mail). Anyone else's pending upload is ignored. With <paramref name="syncDraft"/>, files on
    /// the draft that the composer no longer lists go back to pending. Returns whether the mail has
    /// any attachment afterwards.
    private async Task<bool> LinkAttachmentsAsync(Guid mailId, Guid userId, IReadOnlyList<Guid>? ids, CancellationToken ct, bool syncDraft = false)
    {
        ids ??= [];
        var claimed = 0;
        if (ids.Count > 0)
        {
            var ownDraftMailIds = db.MailRecipients
                .Where(r => r.UserId == userId && r.Folder == "draft" && r.MailId != mailId)
                .Select(r => (Guid?)r.MailId);
            var rows = await db.MailAttachments
                .Where(a => ids.Contains(a.Id)
                    && ((a.MailId == null && a.UploaderId == userId) || ownDraftMailIds.Contains(a.MailId)))
                .ToListAsync(ct);
            foreach (var a in rows)
            {
                a.MailId = mailId;
                a.SortOrder = IndexOf(ids, a.Id);
            }
            claimed = rows.Count;
        }

        var existing = await db.MailAttachments.Where(a => a.MailId == mailId && !ids.Contains(a.Id)).ToListAsync(ct);
        if (syncDraft)
        {
            // Removed in the composer: back to pending for the uploader (the DELETE endpoint or a
            // later draft can still reach it); nothing is deleted here.
            foreach (var a in existing) a.MailId = null;
            existing.Clear();
        }
        return claimed > 0 || existing.Count > 0
            || await db.MailAttachments.AnyAsync(a => a.MailId == mailId && ids.Contains(a.Id), ct);
    }

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (var i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
        return 0;
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

    /// Fase 3 — terapkan aturan label otomatis per penerima saat surat masuk.
    private async Task ApplyAutoRulesAsync(MailEntity mail, IReadOnlyList<User> recipientUsers, CancellationToken ct)
    {
        var recipientIds = recipientUsers.Where(u => u.Id != mail.SenderId).Select(u => u.Id).ToList();
        if (recipientIds.Count == 0) return;

        var rules = await db.MailRules.AsNoTracking()
            .Where(r => recipientIds.Contains(r.UserId) && r.IsEnabled)
            .ToListAsync(ct);
        if (rules.Count == 0) return;

        var sender = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == mail.SenderId, ct);
        var haystacks = new[] { sender?.Name, sender?.Nik, sender?.Email };

        var toAdd = new List<IPop.Core.Entities.MailLabelAssignment>();
        foreach (var rule in rules)
        {
            var needle = rule.SenderContains.Trim();
            var match = haystacks.Any(h => h?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true);
            if (!match) continue;
            var already = await db.MailLabelAssignments.AnyAsync(a => a.LabelId == rule.LabelId && a.MailId == mail.Id, ct);
            if (!already)
                toAdd.Add(new IPop.Core.Entities.MailLabelAssignment
                {
                    Id = Guid.NewGuid(), LabelId = rule.LabelId, MailId = mail.Id, CreatedAt = DateTime.UtcNow
                });
        }
        if (toAdd.Count > 0)
        {
            db.MailLabelAssignments.AddRange(toAdd);
            await db.SaveChangesAsync(ct);
        }
    }

    /// Fase 3 — kirim balasan otomatis (OOO): untuk tiap penerima yang sedang aktif
    /// OOO dan belum membalas pengirim ini hari ini, buat surat balasan otomatis.
    private async Task SendAutoRepliesAsync(MailEntity original, IReadOnlyList<User> recipientUsers, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var recipientIds = recipientUsers.Where(u => u.Id != original.SenderId).Select(u => u.Id).ToList();
        if (recipientIds.Count == 0) return;

        var settings = await db.MailAutoReplySettings.AsNoTracking()
            .Where(s => recipientIds.Contains(s.UserId) && s.IsEnabled)
            .Where(s => (s.FromDate == null || s.FromDate <= today) && (s.UntilDate == null || s.UntilDate >= today))
            .ToListAsync(ct);
        if (settings.Count == 0) return;

        foreach (var s in settings)
        {
            try
            {
                // Satu balasan otomatis per (penerima OOO → pengirim asli) per hari.
                // Dibedakan dari balasan MANUAL lewat marker Signature="auto-reply" —
                // balasan manual yang kebetulan "Re: ..." tidak memblokir OOO berikutnya.
                var alreadyToday = await db.Mails.AnyAsync(m =>
                    m.SenderId == s.UserId &&
                    m.Signature == "auto-reply" &&
                    m.CreatedAt >= today &&
                    m.Recipients.Any(r => r.UserId == original.SenderId && r.RecipientType == "to"), ct);
                if (alreadyToday) continue;

                var reply = new MailEntity
                {
                    Id = Guid.NewGuid(),
                    SenderId = s.UserId,
                    Subject = "Re: " + original.Subject,
                    Body = s.Message ?? "I'm currently out of the office.",
                    IsHtml = false,
                    Signature = "auto-reply",
                    InReplyToId = original.Id,
                    ThreadRootId = original.ThreadRootId ?? original.Id,
                    Status = "sent",
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                };
                db.Mails.Add(reply);
                db.MailRecipients.Add(MakeRecipient(reply.Id, s.UserId, "from", "sent", DateTime.UtcNow));
                db.MailRecipients.Add(MakeRecipient(reply.Id, original.SenderId, "to", "inbox", DateTime.UtcNow));
            }
            catch { /* satu penerima gagal tidak menghentikan yang lain */ }
        }
        await db.SaveChangesAsync(ct);
    }

    private static MailSummaryDto ToSummary(MailRecipient r)
        => ToSummaryFromMail(r.Mail, r.IsStarred, r.IsRead, r.IsPinned, r.IsMuted);

    private static MailSummaryDto ToSummaryFromMail(MailEntity m, bool starred = false, bool read = false, bool pinned = false, bool muted = false) => new(
        m.Id, m.SenderId, m.Sender?.Name ?? "Unknown", m.Sender?.Nik ?? "Unknown",
        m.Subject, Truncate(MailHtml.ToPlainText(m.Body), 120), m.SentAt ?? m.CreatedAt, read, starred,
        m.HasAttachment, m.ThreadCount, m.ThreadRootId is not null || m.InReplyToId is not null,
        m.ThreadRootId, pinned, m.Importance ?? "normal", m.ScheduledAt, muted);

    private static SendMailResult? Validate(ComposeMailRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Subject)) return new SendMailResult(false, null, "subject_required");
        if (req.Subject.Trim().Length > MaxSubjectLength) return new SendMailResult(false, null, "subject_too_long");
        if (string.IsNullOrWhiteSpace(req.Body)) return new SendMailResult(false, null, "body_required");
        if (req.To.Count + req.Cc.Count + req.Bcc.Count == 0 && req.Groups!.Count == 0) return new SendMailResult(false, null, "no_recipients");
        if (req.ScheduledAt is { } at && ScheduleError(at) is { } bad) return new SendMailResult(false, null, bad);
        return ValidateSize(req);
    }

    /// A scheduled time a few minutes in the past (clock skew, slow click) still sends right
    /// away; anything older, or more than a year out, is almost certainly a mistake.
    internal static string? ScheduleError(DateTime at)
    {
        var utc = ToUtc(at);
        var now = DateTime.UtcNow;
        if (utc < now.AddMinutes(-15)) return "schedule_in_past";
        if (utc > now.AddYears(1)) return "schedule_too_far";
        return null;
    }

    private static DateTime ToUtc(DateTime at) => at.Kind switch
    {
        DateTimeKind.Utc => at,
        DateTimeKind.Local => at.ToUniversalTime(),
        _ => DateTime.SpecifyKind(at, DateTimeKind.Utc),
    };

    private static SendMailResult? ValidateDraft(ComposeMailRequest req)
    {
        // Drafts are lenient: subject/body/recipients are all optional (auto-save).
        if (req.Subject.Length > MaxSubjectLength) return new SendMailResult(false, null, "subject_too_long");
        return ValidateSize(req);
    }

    /// Cheap guard before any lookup; the real limit applies to the de-duplicated count.
    private static SendMailResult? ValidateSize(ComposeMailRequest req)
    {
        if (req.To.Count + req.Cc.Count + req.Bcc.Count > MailLimits.MaxRecipients * 3) return new SendMailResult(false, null, "too_many_recipients");
        if ((req.Groups?.Count ?? 0) > 100) return new SendMailResult(false, null, "too_many_groups");
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
