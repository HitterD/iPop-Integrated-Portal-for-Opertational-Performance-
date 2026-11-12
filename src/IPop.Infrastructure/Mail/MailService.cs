using IPop.Application.Attachments;
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

public sealed partial class MailService(IPopDbContext db, INotificationGatewayService? notificationGateway = null, IRealtime? realtime = null,
    MailNotifySignal? notifySignal = null, ILogger<MailService>? log = null, IAttachmentStore? attachments = null) : IMailService
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    private const int MaxSubjectLength = 500;
    private ILogger Log => log ?? NullLogger<MailService>.Instance;
    private MailInlineImages Inline => field ??= new MailInlineImages(db, attachments, Log);

    // ──────────────────────────────────────────────────────────────────────────
    // Compose & delivery
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<SendMailResult> SendAsync(Guid senderId, ComposeMailRequest req, CancellationToken ct)
    {
        req = Normalize(req);
        var validation = Validate(req);
        if (validation is not null) return validation;

        req = await AuthorizeThreadAsync(senderId, req, ct);
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

        // Before the mail is tracked: storing an image saves its Attachment row on this context.
        var inline = await Inline.ExtractAsync(req.Body, senderId, ct);

        var now = DateTime.UtcNow;
        var isScheduled = req.ScheduledAt.HasValue;
        var mail = new MailEntity
        {
            Id = Guid.NewGuid(),
            SenderId = senderId,
            Subject = req.Subject.Trim(),
            Body = inline.Html,
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
        await Inline.LinkAsync(mail.Id, senderId, inline.Ids, replace: false, ct);
        await ApplySenderLabelsAsync(mail.Id, senderId, req.LabelIds, now, ct);
        // Badges/banners/push go through a durable queue written with the inbox rows: the send
        // returns as soon as the mail is in everyone's inbox, and no notification is lost.
        // After the rows exist: the reply's thread root, plus mute carried over to new rows.
        if (!isScheduled) await BumpThreadAsync(mail, now, ct);
        if (!isScheduled) QueueNotify(mail.Id, now);
        await db.SaveChangesAsync(ct);
        if (!isScheduled) await RecountThreadAsync(mail.ThreadRootId, now, ct);

        var recipientUsers = recipients.Select(r => r.User).ToList();
        // Fase 3 — aturan label otomatis: untuk tiap penerima non-pengirim, tempel label
        // dari aturan aktif yang cocok dengan pengirim (nama/NIK/email).
        // The mail is already in every inbox: a rule or auto-reply problem is logged, never
        // reported as a failed send (the sender would retry and everyone would get it twice).
        if (!isScheduled)
            await AfterDeliveryAsync("auto-label rules", mail.Id, () => ApplyAutoRulesAsync(mail, recipientUsers, ct));

        // Fase 3 — balasan otomatis (OOO): tiap penerima yang sedang OOO membalas sekali
        // per hari per pengirim. Not for mass mail: a broadcast would flood the sender.
        if (!isScheduled && !mail.IsMass)
            await AfterDeliveryAsync("auto-replies", mail.Id, () => SendAutoRepliesAsync(mail, recipientUsers, ct));

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

    /// Reply threading comes from the server, never the client: InReplyToId must be a mail the
    /// user actually has (a delivered row), and ThreadRootId is derived from that parent. A
    /// client-supplied ThreadRootId let anyone slip a mail into a conversation they can't see
    /// (and a made-up id hit the FK with a 500).
    private async Task<ComposeMailRequest> AuthorizeThreadAsync(Guid userId, ComposeMailRequest req, CancellationToken ct)
    {
        if (req.InReplyToId is not Guid parentId) return req with { ThreadRootId = null };
        var parent = await db.Mails.AsNoTracking()
            .Where(m => m.Id == parentId && m.Recipients.Any(r => r.UserId == userId && r.Folder != "pending"))
            .Select(m => new { m.Id, m.ThreadRootId })
            .FirstOrDefaultAsync(ct);
        return parent is null
            ? req with { InReplyToId = null, ThreadRootId = null }
            : req with { ThreadRootId = parent.ThreadRootId ?? parent.Id };
    }

    private void QueueNotify(Guid mailId, DateTime now)
        => db.MailNotifyJobs.Add(new MailNotifyJob { Id = Guid.NewGuid(), MailId = mailId, CreatedAt = now, NextAttemptAt = now });

    public async Task<SendMailResult> SaveDraftAsync(Guid userId, ComposeMailRequest req, CancellationToken ct)
    {
        req = Normalize(req);
        var validation = ValidateDraft(req);
        if (validation is not null) return validation;
        req = await AuthorizeThreadAsync(userId, req, ct);

        var inline = await Inline.ExtractAsync(req.Body, userId, ct);
        var now = DateTime.UtcNow;
        var mail = new MailEntity
        {
            Id = Guid.NewGuid(),
            SenderId = userId,
            Subject = req.Subject.Trim(),
            Body = inline.Html,
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
        await Inline.LinkAsync(mail.Id, userId, inline.Ids, replace: false, ct);
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
        req = await AuthorizeThreadAsync(userId, req, ct);

        var recipient = await db.MailRecipients
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder == "draft", ct);
        if (recipient is null) return new SendMailResult(false, null, "draft_not_found");

        var mail = await db.Mails.FindAsync([mailId], ct);
        // Only the author's own unsent mail is a draft. The folder alone is not proof: it belongs to
        // the user's row, and a sent mail edited here would change everyone's copy.
        if (mail is null || mail.SenderId != userId || mail.Status != "draft")
            return new SendMailResult(false, null, "draft_not_found");

        // Autosave sends the same pasted image again and again: the store step re-uses it by hash.
        var inline = await Inline.ExtractAsync(req.Body, userId, ct);
        mail.Subject = req.Subject.Trim();
        mail.Body = inline.Html;
        mail.Signature = req.Signature;
        mail.Importance = string.IsNullOrWhiteSpace(req.Importance) ? "normal" : req.Importance;
        mail.InReplyToId = req.InReplyToId;
        mail.ThreadRootId = req.ThreadRootId ?? req.InReplyToId;
        mail.DraftState = SerializeDraftState(req);
        mail.UpdatedAt = DateTime.UtcNow;
        mail.HasAttachment = await LinkAttachmentsAsync(mail.Id, userId, req.AttachmentIds, ct, syncDraft: true);
        await Inline.LinkAsync(mail.Id, userId, inline.Ids, replace: true, ct);
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
        if (!await db.MailRecipients.AnyAsync(r => r.MailId == originalMailId && r.UserId == senderId && r.Folder != "pending", ct))
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

    /// Mail text search reports "1000+" past this many matches (TotalCount = SearchCountCap + 1).
    public const int SearchCountCap = 1000;

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
                // LIKE (not ILIKE) so SQLite tests and Postgres translate the same query. Postgres
                // LIKE is case-sensitive, so both sides are lower-cased: "rapat" must find "Rapat".
                var pattern = EscapeLikePattern(search.Query.ToLowerInvariant());
                query = query.Where(r =>
                    EF.Functions.Like(r.Mail.Subject.ToLower(), pattern) ||
                    EF.Functions.Like(r.Mail.Body.ToLower(), pattern) ||
                    EF.Functions.Like(r.Mail.Sender.Name.ToLower(), pattern) ||
                    EF.Functions.Like(r.Mail.Sender.Nik.ToLower(), pattern));
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
            // The picker sends calendar days ("2026-10-01", Kind=Unspecified). Npgsql refuses an
            // Unspecified DateTime against timestamptz (the filter used to 500), and the day must
            // be the user's own day, not UTC's. Half-open range: [from 00:00, to+1 00:00).
            if (search.DateFrom is not null || search.DateTo is not null)
            {
                var tz = await UserTimeZoneAsync(userId, ct);
                if (search.DateFrom is { } from)
                {
                    var f = LocalDayStartUtc(from, tz);
                    query = query.Where(r => (r.Mail.SentAt ?? r.Mail.CreatedAt) >= f);
                }
                if (search.DateTo is { } to)
                {
                    var t = LocalDayStartUtc(to.Date.AddDays(1), tz);
                    query = query.Where(r => (r.Mail.SentAt ?? r.Mail.CreatedAt) < t);
                }
            }
            // Punya lampiran (Surat Lanjutan).
            if (search.HasAttachment == true)
                query = query.Where(r => r.Mail.HasAttachment);
            if (!string.IsNullOrWhiteSpace(search.Importance))
                query = query.Where(r => r.Mail.Importance == search.Importance.ToLowerInvariant().Trim());
        }

        // A text search counts at most SearchCountCap matches: the full count joined every mail I ever got
        // (11 s at ten years of mail); the page itself walks my newest rows and stops once it is full.
        var totalCount = string.IsNullOrWhiteSpace(search?.Query)
            ? await query.CountAsync(ct)
            : await query.OrderByDescending(r => r.IsPinned).ThenByDescending(r => r.CreatedAt).Take(SearchCountCap + 1).CountAsync(ct);   // index order, so it stops at the cap

        var items = await query
            .Include(r => r.Mail).ThenInclude(m => m.Sender)
            // Sorted on the row's own time so (UserId, Folder, IsPinned, CreatedAt) serves the page: sorting on
            // the joined Mail's SentAt read every inbox row first (17k rows, 1.2 s at ten years of mail).
            // A recipient row is stamped when it is delivered (scheduled mail too), so the order is the same.
            .OrderByDescending(r => r.IsPinned)
            .ThenByDescending(r => r.CreatedAt)
            // Tie-breaker: a scheduled batch shares one time, and without a unique key Skip/Take
            // could repeat or drop a message between "Load more" pages.
            .ThenByDescending(r => r.MailId)
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
            .GroupBy(r => new { r.Folder, r.IsRead, r.IsStarred, r.IsMuted })
            .Select(g => new { g.Key.Folder, g.Key.IsRead, g.Key.IsStarred, g.Key.IsMuted, Count = g.Count() })
            .ToListAsync(ct);

        int Get(string folder) => rows.Where(r => r.Folder == folder).Sum(r => r.Count);
        // Muted threads don't count as unread (same rule as the Mail badge in the sidebar).
        int GetUnread(string folder) => rows.Where(r => r.Folder == folder && !r.IsRead && !r.IsMuted).Sum(r => r.Count);

        return new FolderCountsDto(
            GetUnread("inbox"), Get("inbox"),
            Get("sent"), Get("draft"), rows.Where(r => r.IsStarred).Sum(r => r.Count),
            Get("archive"), Get("trash"), Get("scheduled"),
            rows.Where(r => r.Folder == "inbox" && r.IsStarred).Sum(r => r.Count));
    }

    public async Task<PagedResult<MailSummaryDto>> GetThreadAsync(Guid userId, Guid threadRootId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 100) pageSize = 100;

        var involvedMailIds = db.MailRecipients.Where(r => r.UserId == userId && r.Folder != "pending").Select(r => r.MailId);

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
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder != "pending", ct);
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
            // The reader always makes the cut, so a mass-mail recipient past the first 50 still reads "you and N others".
            .OrderBy(r => r.UserId == userId && r.RecipientType != "from" ? 0 : 1)
            .ThenBy(r => r.RecipientType == "to" ? 0 : r.RecipientType == "cc" ? 1 : r.RecipientType == "bcc" ? 2 : 3)
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
            // Plain-text bodies (older out-of-office replies) go out as encoded HTML: every client
            // sink (reading pane, reply quote, forward) treats the body as markup.
            mail.Subject, mail.IsHtml ? (draftState is not null || mail.Status == "draft" ? MailHtml.Sanitize(mail.Body) : MailHtml.ForDisplay(mail.Body)) : PlainToHtml(mail.Body), true, mail.Signature, mail.SentAt,
            mail.InReplyToId, mail.ThreadRootId, mail.ThreadCount,
            recipient.IsStarred, recipient.IsRead, recipient.Folder,
            mail.PdfExportRequestedBy, mail.PdfExportRequestedAt,
            visibleRecipients
                .OrderBy(r => r.UserId == userId && r.RecipientType != "from" ? 0 : 1)
                .ThenBy(r => r.RecipientType == "to" ? 0 : r.RecipientType == "cc" ? 1 : r.RecipientType == "bcc" ? 2 : 3)
                // The author reopening a draft needs every typed recipient back in the composer.
                .Take(draftState is not null ? int.MaxValue : MailLimits.DetailRecipients)
                .Select(r => new MailRecipientDto(r.UserId, r.User.Name, r.User.Nik, r.User.Email, r.RecipientType))
                .ToList(),
            mail.Attachments.OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt)
                .Select(a => new AttachmentDto(a.Id, a.FileName, a.FileSize, a.MimeType, a.ThumbnailPath)).ToList(),
            // "Read" here is the strict SeenAt (opened and seen), never ReadAt: that is the recipient's own unread
            // flag (Mark all read, bulk read) and is nobody else's business.
            visibleDelivery
                .Select(r => new DeliveryReadStateDto(r.UserId, r.User.Name, r.User.Nik, r.RecipientType, r.DeliveredAt,
                    r.UserId == userId ? r.ReadAt : r.SeenAt))
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
            SenderIsActive = mail.Sender.IsActive,
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

    /// Everyone a Reply all should address, complete: the typed To/Cc people (not just the first
    /// 50 the detail carries) and the To/Cc groups as chips (members are never expanded, so a
    /// reply to a group goes to the group's current members). Bcc never. Null when the caller may
    /// not reply-all (mass / hidden-list mail of someone else) or isn't a participant.
    public async Task<ReplyAllDto?> GetReplyAllAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var mail = await db.Mails.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mailId, ct);
        if (mail is null || !await db.MailRecipients.AnyAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder != "pending", ct)) return null;
        var callerIsSender = mail.SenderId == userId;
        if (!callerIsSender && (mail.IsMass || mail.HideRecipients)) return null;
        var people = await VisibleRecipients(mail, userId)
            .Where(r => (r.RecipientType == "to" || r.RecipientType == "cc") && !r.ViaGroup)
            .OrderBy(r => r.RecipientType == "to" ? 0 : 1).ThenBy(r => r.CreatedAt)
            .Take(MailLimits.MaxRecipients)
            .Select(r => new MailRecipientDto(r.UserId, r.User.Name, r.User.Nik, r.User.Email, r.RecipientType))
            .ToListAsync(ct);
        var groups = VisibleGroups(mail, callerIsSender)
            .Where(g => g.Field is "to" or "cc" && g.Id != Guid.Empty).ToList();
        return new ReplyAllDto(people, groups);
    }

    public async Task<PagedResult<MailRecipientDto>?> GetRecipientsAsync(Guid userId, Guid mailId, int page, int pageSize, CancellationToken ct)
    {
        var mail = await db.Mails.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mailId, ct);
        if (mail is null || !await db.MailRecipients.AnyAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder != "pending", ct)) return null;
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
            .Select(r => new DeliveryReadStateDto(r.UserId, r.User.Name, r.User.Nik, r.RecipientType, r.DeliveredAt, r.SeenAt))
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

    /// Where the user's own row ends up when they move it to <paramref name="folder"/>, or null when
    /// the move is not allowed. Draft/Sent/Scheduled are lifecycle folders, not user targets: letting
    /// a recipient drop a received mail into "draft" made it an editable draft of somebody else's
    /// mail. "Inbox" and "Sent" both mean "back to where it belongs" — the author's copy returns to
    /// Sent, everyone else's to the Inbox (so an undo of "archive" works from either folder).
    private static string? MoveTarget(MailRecipient row, string? mailStatus, MailFolder folder)
    {
        // Rows that are not delivered yet, or still being written, only move via the
        // draft/schedule actions (Discard, Cancel, Send now, Delete).
        if (row.Folder is "pending" or "draft" or "scheduled") return null;
        return folder switch
        {
            MailFolder.Archive => "archive",
            MailFolder.Trash => "trash",
            MailFolder.Inbox or MailFolder.Sent => RestoreFolder(row, mailStatus),
            _ => null,
        };
    }

    /// Where a row goes back to when it leaves Trash: the author's copy follows the mail's status,
    /// everyone else's copy returns to the Inbox.
    private static string RestoreFolder(MailRecipient row, string? mailStatus) => row.RecipientType == "from"
        ? mailStatus switch { "draft" => "draft", "scheduled" => "scheduled", _ => "sent" }
        : "inbox";

    public async Task<SendMailResult> MoveToFolderAsync(Guid userId, Guid mailId, MailFolder folder, CancellationToken ct)
    {
        var recipient = await FindRecipientAsync(userId, mailId, ct);
        if (recipient is null) return new SendMailResult(false, null, "mail_not_found");
        if (folder == MailFolder.Starred) return new SendMailResult(false, null, "use_starred_flag");

        if (recipient.Folder == "trash" && FolderToString(folder) != "trash")
            return new SendMailResult(false, null, "mail_trashed");

        var status = await db.Mails.Where(m => m.Id == mailId).Select(m => m.Status).FirstOrDefaultAsync(ct);
        var folderStr = recipient.Folder == "trash" ? "trash" : MoveTarget(recipient, status, folder);
        if (folderStr is null) return new SendMailResult(false, null, "invalid_folder");
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

        // Deleting a scheduled mail must stop it: the scheduler goes by the mail's status, so a
        // trashed Scheduled row used to be delivered anyway — with Cancel no longer reachable.
        if (recipient.Folder == "scheduled")
        {
            var cancelled = await CancelScheduleAsync(userId, mailId, ct);
            if (!cancelled.Success) return cancelled;
            recipient = await FindRecipientAsync(userId, mailId, ct);
            if (recipient is null) return new SendMailResult(false, null, "mail_not_found");
        }

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
        // One DELETE instead of tracking and removing every row.
        var count = await db.MailRecipients.Where(r => r.UserId == userId && r.Folder == "trash").ExecuteDeleteAsync(ct);
        if (count == 0) return 0;
        await SyncUserAsync(userId, ct);
        return count;
    }

    public async Task<bool> ToggleMuteAsync(Guid userId, Guid mailId, CancellationToken ct)
    {
        var recipient = await db.MailRecipients.FirstOrDefaultAsync(r => r.UserId == userId && r.MailId == mailId && r.Folder != "pending", ct);
        if (recipient is null) return false;

        var muted = !recipient.IsMuted;
        // Mute is a conversation setting: every message of the thread in this user's mailbox
        // follows, so the unread badge and push agree (push already went by the whole thread).
        var rootId = await db.Mails.Where(m => m.Id == mailId).Select(m => m.ThreadRootId ?? m.Id).FirstAsync(ct);
        var rows = await db.MailRecipients
            .Where(r => r.UserId == userId && r.Folder != "pending" && (r.MailId == rootId || r.Mail.ThreadRootId == rootId))
            .ToListAsync(ct);
        if (!rows.Contains(recipient)) rows.Add(recipient);
        foreach (var r in rows) r.IsMuted = muted;
        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
        return muted;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Per-user folder actions — bulk
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<BulkResultDto> BulkMoveAsync(Guid userId, IReadOnlyList<Guid> mailIds, MailFolder folder, CancellationToken ct)
    {
        if (mailIds.Count == 0) return new BulkResultDto(0);
        if (folder == MailFolder.Starred) return new BulkResultDto(0, "use_starred_flag");
        if (folder is not (MailFolder.Inbox or MailFolder.Sent or MailFolder.Archive or MailFolder.Trash))
            return new BulkResultDto(0, "invalid_folder");

        var rows = await db.MailRecipients
            .Include(r => r.Mail)
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId) && r.Folder != "trash")
            .ToListAsync(ct);
        var moved = 0;
        foreach (var r in rows)
        {
            if (MoveTarget(r, r.Mail.Status, folder) is not { } target) continue;
            r.Folder = target;
            moved++;
        }
        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
        return new BulkResultDto(moved);
    }

    public async Task<BulkResultDto> BulkToggleStarAsync(Guid userId, IReadOnlyList<Guid> mailIds, CancellationToken ct)
    {
        if (mailIds.Count == 0) return new BulkResultDto(0);
        var rows = await db.MailRecipients
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId) && r.Folder != "pending")
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
        // Scheduled mails are cancelled first (see DeleteAsync) so trashing them stops delivery.
        var scheduled = await db.MailRecipients
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId) && r.Folder == "scheduled")
            .Select(r => r.MailId).ToListAsync(ct);
        foreach (var id in scheduled) await CancelScheduleAsync(userId, id, ct);

        var rows = await db.MailRecipients
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId) && r.Folder != "pending" && r.Folder != "scheduled")
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
            .Include(r => r.Mail)
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId) && r.Folder == "trash")
            .ToListAsync(ct);
        // A restored Sent mail used to land in the author's own Inbox, and a restored draft became
        // unreachable (Inbox rows can't be edited or discarded as drafts).
        foreach (var row in rows) row.Folder = RestoreFolder(row, row.Mail.Status);
        await db.SaveChangesAsync(ct);
        await SyncUserAsync(userId, ct);
        return new BulkResultDto(rows.Count);
    }

    public async Task<BulkResultDto> BulkMarkReadAsync(Guid userId, IReadOnlyList<Guid> mailIds, CancellationToken ct)
    {
        if (mailIds.Count == 0) return new BulkResultDto(0);
        var now = DateTime.UtcNow;
        var rows = await db.MailRecipients
            .Where(r => r.UserId == userId && mailIds.Contains(r.MailId) && !r.IsRead && r.Folder != "pending")
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
        // One UPDATE instead of loading and saving every row (an imported 2,760-mail inbox was 2,760 UPDATEs).
        var count = await db.MailRecipients
            .Where(r => r.UserId == userId && r.Folder == folderStr && !r.IsRead)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.IsRead, true).SetProperty(r => r.ReadAt, r => r.ReadAt ?? now), ct);
        await SyncUserAsync(userId, ct);
        return new BulkResultDto(count);
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
        // Claim + changes commit together: a failed save must not leave a "draft" whose
        // recipients still sit in "pending".
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Too late if the scheduler (or Send now in another tab) already took it.
        if (!await ClaimScheduledAsync(mailId, "draft", ct)) return new SendMailResult(false, null, "already_sent");

        mail.Status = "draft";
        mail.ScheduledAt = null;
        mail.CancelledAt = DateTime.UtcNow;
        mail.UpdatedAt = DateTime.UtcNow;

        senderRecipient.Folder = "draft";

        var pendingTargets = mail.Recipients.Where(r => r.UserId != userId && r.Folder == "pending").ToList();
        // Back to an ordinary draft, whose recipients live in DraftState: without this, reopening
        // it from Drafts showed an empty To line. Group members stay summarised by their group.
        if (mail.DraftState is null)
        {
            var typed = pendingTargets.Where(r => !r.ViaGroup).ToList();
            var nikById = await db.Users.AsNoTracking()
                .Where(u => typed.Select(r => r.UserId).Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Nik, ct);
            List<string> Niks(string type) => typed.Where(r => r.RecipientType == type && nikById.ContainsKey(r.UserId))
                .Select(r => nikById[r.UserId]).ToList();
            var groups = VisibleGroups(mail, callerIsSender: true).Select(g => new MailGroupRef(g.Field, g.Kind, g.Id)).ToList();
            mail.DraftState = System.Text.Json.JsonSerializer.Serialize(new DraftStateJson(
                Niks("to"), Niks("cc"), Niks("bcc"), null, groups.Count > 0 ? groups : null, mail.HideRecipients));
        }
        mail.IsMass = false;
        mail.RecipientCount = 0;
        mail.RecipientGroupsJson = null;
        foreach (var p in pendingTargets)
        {
            db.MailRecipients.Remove(p);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await SyncUserAsync(userId, ct);
        return new SendMailResult(true, mailId, null);
    }

    public async Task<SendMailResult> RescheduleAsync(Guid userId, Guid mailId, DateTime scheduledAt, CancellationToken ct)
    {
        var senderRecipient = await db.MailRecipients
            .FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder == "scheduled", ct);
        if (senderRecipient is null) return new SendMailResult(false, null, "mail_not_found");

        if (ScheduleError(scheduledAt) is { } bad) return new SendMailResult(false, null, bad);
        // One conditional UPDATE: if the scheduler or Send now already took the mail, say so
        // instead of reporting "rescheduled" for a mail that has gone out.
        var utc = ToUtc(scheduledAt);
        var now = DateTime.UtcNow;
        var changed = await db.Mails
            .Where(m => m.Id == mailId && m.SenderId == userId && m.Status == "scheduled")
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.ScheduledAt, utc).SetProperty(m => m.UpdatedAt, now), ct);
        if (changed == 0) return new SendMailResult(false, null, "already_sent");
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
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await ClaimScheduledAsync(mailId, "sent", ct)) return new SendMailResult(false, null, "already_sent");

        var now = DateTime.UtcNow;
        mail.Status = "sent";
        mail.SentAt = now;
        mail.ScheduledAt = now;
        mail.UpdatedAt = now;
        await BumpThreadAsync(mail, now, ct);

        senderRecipient.Folder = "sent";
        senderRecipient.CreatedAt = now;   // folders sort on the row's time: it is sent now, not when it was scheduled
        var delivered = mail.Recipients.Where(r => r.Folder == "pending").ToList();
        foreach (var r in delivered)
        {
            r.Folder = "inbox";
            r.DeliveredAt = now;
            r.CreatedAt = now;
        }
        QueueNotify(mail.Id, now);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await RecountThreadAsync(mail.ThreadRootId, now, ct);
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
            // Stable per mail: a retry after a failed job save returns the existing notification (no second toast).
            idempotencyKey: $"new-{mail.Id:N}",
            priority: mail.Importance == "high" ? "high" : "normal",
            targetUrl: $"/mail?mailId={mail.Id}",
            ct: ct);
        // A rejected request (validation) won't succeed on retry; only exceptions are retried.
        if (result is { Success: false }) job.LastError = Truncate($"{result.ErrorCode}: {result.Error}", 1000);
    }

    public async Task<int> DeliverDueScheduledMailsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var dueIds = await db.Mails.AsNoTracking()
            .Where(m => m.Status == "scheduled" && m.ScheduledAt != null && m.ScheduledAt <= now)
            .OrderBy(m => m.ScheduledAt)
            .Select(m => m.Id)
            .Take(200) // the rest go on the next tick
            .ToListAsync(ct);

        if (dueIds.Count == 0) return 0;

        // One mail per transaction. The claim (a conditional UPDATE) used to commit on its own
        // while the folder moves were saved later in one batch: if that save failed, the mail was
        // already "sent" but its recipients stayed "pending" forever — never delivered, and no
        // longer cancellable. Now the claim and the delivery commit or roll back together.
        var delivered = new List<MailEntity>();
        foreach (var id in dueIds)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Claim it: a Cancel, Send now or Reschedule that won the race leaves nothing to deliver.
                if (!await ClaimScheduledAsync(id, "sent", ct, dueBy: now)) { await tx.RollbackAsync(ct); continue; }
                var mail = await db.Mails.Include(m => m.Recipients).FirstAsync(m => m.Id == id, ct);
                mail.Status = "sent";
                mail.SentAt = now;
                await BumpThreadAsync(mail, now, ct);

                foreach (var r in mail.Recipients)
                {
                    if (r.RecipientType == "from" && r.Folder == "scheduled")
                    {
                        r.Folder = "sent";
                        r.CreatedAt = now;
                    }
                    else if (r.Folder == "pending")
                    {
                        r.Folder = "inbox";
                        r.DeliveredAt = now;
                        r.CreatedAt = now;
                    }
                }
                // Scheduled mail used to land silently: no badge, no banner.
                QueueNotify(mail.Id, now);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                delivered.Add(mail);
                await AfterDeliveryAsync("thread count", mail.Id, () => RecountThreadAsync(mail.ThreadRootId, now, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                Log.LogError(ex, "Scheduled mail {MailId} could not be delivered; it stays scheduled and is retried next tick.", id);
            }
        }
        // Same follow-ups as an immediate send: recipients' auto-label rules and out-of-office replies.
        foreach (var mail in delivered)
        {
            var ids = mail.Recipients.Where(r => r.RecipientType != "from").Select(r => r.UserId).Distinct().ToList();
            var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);
            await AfterDeliveryAsync("auto-label rules", mail.Id, () => ApplyAutoRulesAsync(mail, users, ct));
            if (!mail.IsMass)
                await AfterDeliveryAsync("auto-replies", mail.Id, () => SendAutoRepliesAsync(mail, users, ct));
        }
        foreach (var senderId in delivered.Select(m => m.SenderId).Distinct())
            await SyncUserAsync(senderId, ct);
        notifySignal?.Notify();
        return delivered.Count;
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

    private async Task<TimeZoneInfo> UserTimeZoneAsync(Guid userId, CancellationToken ct)
        => IPop.Application.MeetingRooms.RoomBookingRules.ResolveZone(
            await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZoneId).FirstOrDefaultAsync(ct));

    /// Midnight of <paramref name="day"/>'s calendar date in <paramref name="tz"/>, as a UTC instant.
    internal static DateTime LocalDayStartUtc(DateTime day, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified), tz);

    private async Task<MailRecipient?> FindRecipientAsync(Guid userId, Guid mailId, CancellationToken ct)
        => await db.MailRecipients.FirstOrDefaultAsync(r => r.MailId == mailId && r.UserId == userId && r.Folder != "pending", ct);

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
            // varchar(16) column, and the list filters only know these three: anything else used
            // to either 500 on save or silently drop out of the High/Low filters.
            Importance = req.Importance?.Trim().ToLowerInvariant() is "high" or "low" ? req.Importance.Trim().ToLowerInvariant() : "normal",
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
            // The user's own drafts by authorship + status, not by folder: a folder is per-user
            // state and must never decide who may take a file off a mail.
            var ownDraftMailIds = db.Mails
                .Where(m => m.SenderId == userId && m.Status == "draft" && m.Id != mailId)
                .Select(m => (Guid?)m.Id);
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
    /// Resolves the reply's thread root (always through the parent, so replies to replies stay
    /// in the original conversation) and carries a muted thread over to the new reply's rows.
    /// The count itself is recomputed after the save by <see cref="RecountThreadAsync"/>.
    private async Task BumpThreadAsync(MailEntity mail, DateTime now, CancellationToken ct)
    {
        if (mail.InReplyToId is null) return;
        var parent = await db.Mails.AsNoTracking().Where(m => m.Id == mail.InReplyToId.Value)
            .Select(m => new { m.Id, m.ThreadRootId }).FirstOrDefaultAsync(ct);
        if (parent is null) return;
        mail.ThreadRootId = parent.ThreadRootId ?? parent.Id;
        await InheritMuteAsync(mail, ct);
    }

    /// Mute is per thread for the reader: a reply arriving in a conversation they muted starts
    /// muted, so it no longer raises the unread badge (push already skipped muted threads).
    private async Task InheritMuteAsync(MailEntity mail, CancellationToken ct)
    {
        if (mail.ThreadRootId is not { } rootId) return;
        var rows = db.ChangeTracker.Entries<MailRecipient>()
            .Where(e => e.Entity.MailId == mail.Id && e.Entity.RecipientType != "from")
            .Select(e => e.Entity).ToList();
        if (rows.Count == 0) rows = mail.Recipients.Where(r => r.RecipientType != "from").ToList();
        if (rows.Count == 0) return;
        var userIds = rows.Select(r => r.UserId).Distinct().ToList();
        var muted = (await db.MailRecipients.AsNoTracking()
            .Where(r => userIds.Contains(r.UserId) && r.IsMuted && r.MailId != mail.Id
                && (r.MailId == rootId || r.Mail.ThreadRootId == rootId))
            .Select(r => r.UserId).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var r in rows) if (muted.Contains(r.UserId)) r.IsMuted = true;
    }

    /// "n replies" on the thread root, recomputed in the database in one statement after the
    /// reply is saved: counting in memory raced with concurrent sends (two replies in one
    /// scheduler batch both read the same count) and also counted drafts, scheduled and
    /// recalled replies — a scheduled reply even twice (+1 on top of its own row).
    private async Task RecountThreadAsync(Guid? rootId, DateTime now, CancellationToken ct)
    {
        if (rootId is not { } id) return;
        await db.Mails.Where(m => m.Id == id).ExecuteUpdateAsync(u => u
            // Number of replies (the root itself is not counted), counted after the reply is saved.
            .SetProperty(m => m.ThreadCount, m => db.Mails.Count(x => x.ThreadRootId == id && x.Status == "sent"))
            .SetProperty(m => m.LastThreadAt, now), ct);
    }

    /// Moves a scheduled mail to <paramref name="status"/> only if it is still scheduled, in one
    /// conditional UPDATE: of a Cancel, a Send now and the scheduler racing, exactly one wins.
    /// With <paramref name="dueBy"/> (the scheduler), the mail must also still be due: a Reschedule
    /// saved after the batch was read wins, instead of the mail going out at the old time.
    private async Task<bool> ClaimScheduledAsync(Guid mailId, string status, CancellationToken ct, DateTime? dueBy = null) =>
        await db.Mails.Where(m => m.Id == mailId && m.Status == "scheduled"
                && (dueBy == null || (m.ScheduledAt != null && m.ScheduledAt <= dueBy)))
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, status), ct) == 1;

    private async Task AfterDeliveryAsync(string step, Guid mailId, Func<Task> run)
    {
        try { await run(); }
        catch (Exception ex)
        {
            db.ChangeTracker.Clear();
            Log.LogError(ex, "Mail {MailId}: {Step} failed after delivery; the mail itself was sent.", mailId, step);
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
            // Two rules can point at the same label: add each label once.
            if (toAdd.Any(t => t.LabelId == rule.LabelId)) continue;
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
        var nowUtc = DateTime.UtcNow;
        var recipientIds = recipientUsers.Where(u => u.Id != original.SenderId).Select(u => u.Id).ToList();
        if (recipientIds.Count == 0) return;

        // The settings dialog stores the user's local midnight as a UTC instant (Jakarta 30 Oct →
        // 29 Oct 17:00Z). Comparing against UTC's calendar date switched OOO off at 07:00 on the
        // last day and on only at 07:00 on the first: compare instants, with Until inclusive.
        var settings = (await db.MailAutoReplySettings.AsNoTracking()
            .Where(s => recipientIds.Contains(s.UserId) && s.IsEnabled)
            .Where(s => s.FromDate == null || s.FromDate <= nowUtc)
            .ToListAsync(ct))
            .Where(s => s.UntilDate is null || nowUtc < AsUtc(s.UntilDate.Value).AddDays(1))
            .ToList();
        if (settings.Count == 0) return;

        var queued = false;
        foreach (var s in settings)
        {
            try
            {
                // "Once a day" is the OOO user's own day, not UTC's.
                var tz = IPop.Application.MeetingRooms.RoomBookingRules.ResolveZone(
                    recipientUsers.FirstOrDefault(u => u.Id == s.UserId)?.TimeZoneId);
                var today = LocalDayStartUtc(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz), tz);
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
                    // Stored as HTML (encoded text, line breaks kept): the message is user input, and
                    // a plain-text body was rendered as markup by the reading pane (stored XSS).
                    Body = PlainToHtml(s.Message ?? "I'm currently out of the office."),
                    IsHtml = true,
                    Signature = "auto-reply",
                    InReplyToId = original.Id,
                    ThreadRootId = original.ThreadRootId ?? original.Id,
                    Status = "sent",
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                };
                db.Mails.Add(reply);
                db.MailRecipients.Add(MakeRecipient(reply.Id, s.UserId, "from", "sent", DateTime.UtcNow));
                var inboxRow = MakeRecipient(reply.Id, original.SenderId, "to", "inbox", DateTime.UtcNow);
                inboxRow.DeliveredAt = DateTime.UtcNow;
                db.MailRecipients.Add(inboxRow);
                await InheritMuteAsync(reply, ct);
                // Same durable notification as any other mail: badge, banner and toast for the sender.
                QueueNotify(reply.Id, DateTime.UtcNow);
                queued = true;
            }
            catch { /* satu penerima gagal tidak menghentikan yang lain */ }
        }
        await db.SaveChangesAsync(ct);
        // Auto-replies are replies too: they used to be missing from the thread's count.
        if (queued) await RecountThreadAsync(original.ThreadRootId ?? original.Id, DateTime.UtcNow, ct);
        if (queued) notifySignal?.Notify();
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

    internal static string PlainToHtml(string text)
        => "<p>" + System.Net.WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>") + "</p>";

    private static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    private static string EscapeLikePattern(string query)
        => $"%{query.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..maxLength] + "...";
    }
}
