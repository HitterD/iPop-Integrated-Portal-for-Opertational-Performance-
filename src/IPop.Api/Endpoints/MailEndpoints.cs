using IPop.Api.Rbac;
using IPop.Application.Files;
using IPop.Application.Mail;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class MailEndpoints
{
    public static IEndpointRouteBuilder MapMail(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/mail").RequireAuthorization().RequirePermission("mail.read");

        // ──────────────────────────────────────────────────────────────────────
        // Folder queries
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/mail/folders/{folder}?page=1&pageSize=20&q=&starredOnly=&unreadOnly=&senderNik=
        g.MapGet("/folders/{folder}", async (string folder, int? page, int? pageSize, string? q, bool? starredOnly, bool? unreadOnly, string? senderNik, string? label,
            DateTime? dateFrom, DateTime? dateTo, bool? hasAttachment, string? importance,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var parsed = folder.ToLowerInvariant() switch
            {
                "inbox" => MailFolder.Inbox,
                "sent" => MailFolder.Sent,
                "draft" => MailFolder.Draft,
                "starred" => MailFolder.Starred,
                "archive" => MailFolder.Archive,
                "trash" => MailFolder.Trash,
                "scheduled" => MailFolder.Scheduled,
                _ => MailFolder.Inbox
            };
            var search = new MailSearchRequest(q, starredOnly, unreadOnly, senderNik, label, dateFrom, dateTo, hasAttachment, importance);
            var result = await svc.GetFolderAsync(userId.Value, parsed, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 20, 1, 100), search, ct);
            return Results.Ok(result);
        });

        // GET /api/mail/counts — sidebar badge numbers in one call.
        g.MapGet("/counts", async (HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await svc.GetFolderCountsAsync(userId.Value, ct));
        });

        // GET /api/mail/threads/{threadRootId}?page=1&pageSize=50
        g.MapGet("/threads/{threadRootId:guid}", async (Guid threadRootId, int? page, int? pageSize,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.GetThreadAsync(userId.Value, threadRootId, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, 100), ct);
            return Results.Ok(result);
        });

        // ──────────────────────────────────────────────────────────────────────
        // Composer
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/mail/send — { subject, body, to[], cc[], bcc[], attachmentIds[], inReplyToId, threadRootId, signature }
        g.MapPost("/send", async (ComposeMailRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.SendAsync(userId.Value, req, ct);
            if (!result.Success) return SendError(result);
            await WriteAuditAsync(http, db, "mail.send", result.MailId!.Value.ToString(), ct);
            await AuditMassAsync(http, db, result, ct);
            return Results.Ok(result);
        }).RequirePermission("mail.send")
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxComposeRequestBytes));

        // POST /api/mail/drafts — save a new draft (auto-save).
        g.MapPost("/drafts", async (ComposeMailRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.SaveDraftAsync(userId.Value, req, ct);
            if (!result.Success)
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: ErrorStatus(result.Error));
            return Results.Ok(result);
        }).RequirePermission("mail.send")
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxComposeRequestBytes));

        // PUT /api/mail/drafts/{id} — update an existing draft.
        g.MapPut("/drafts/{id:guid}", async (Guid id, ComposeMailRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.UpdateDraftAsync(userId.Value, id, req, ct);
            if (!result.Success)
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: ErrorStatus(result.Error));
            return Results.Ok(result);
        }).RequirePermission("mail.send")
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxComposeRequestBytes));

        // DELETE /api/mail/drafts/{id} — discard a draft.
        g.MapDelete("/drafts/{id:guid}", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.DiscardDraftAsync(userId.Value, id, ct);
            if (!result.Success)
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: ErrorStatus(result.Error));
            await WriteAuditAsync(http, db, "mail.draft.discard", id.ToString(), ct);
            return Results.Ok(result);
        }).RequirePermission("mail.send");

        // POST /api/mail/{id}/forward — { subject, body, to[], cc[], bcc[], attachmentIds[], signature }
        g.MapPost("/{id:guid}/forward", async (Guid id, ComposeMailRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.ForwardAsync(userId.Value, id, req, ct);
            if (!result.Success) return SendError(result);
            await WriteAuditAsync(http, db, "mail.forward", id.ToString(), ct);
            await AuditMassAsync(http, db, result, ct);
            return Results.Ok(result);
        }).RequirePermission("mail.send")
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxComposeRequestBytes));

        // POST /api/mail/recipients/preview — the compose payload expanded server-side: the
        // de-duplicated count and breakdown shown in the "Send to N people?" dialog.
        g.MapPost("/recipients/preview", async (ComposeMailRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await svc.PreviewRecipientsAsync(userId.Value, req, ct));
        }).RequirePermission("mail.send");

        // GET /api/mail/{id}/stats — sender only: sent / delivered / read counts + skipped.
        g.MapGet("/{id:guid}/stats", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var stats = await svc.GetStatsAsync(userId.Value, id, ct);
            return stats is not null ? Results.Ok(stats) : Results.Json(new { code = "mail_not_found", message = "Mail not found." }, statusCode: 404);
        });

        // GET /api/mail/{id}/readers?read=true&page=1 — sender only: who has (not) read it, 50 a page.
        g.MapGet("/{id:guid}/readers", async (Guid id, bool? read, int? page, int? pageSize,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.GetReadersAsync(userId.Value, id, read ?? true, page ?? 1, pageSize ?? 50, ct);
            return result is not null ? Results.Ok(result) : Results.Json(new { code = "mail_not_found", message = "Mail not found." }, statusCode: 404);
        });

        // GET /api/mail/{id}/reply-all — everyone a Reply all addresses (typed people + group chips).
        g.MapGet("/{id:guid}/reply-all", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.GetReplyAllAsync(userId.Value, id, ct);
            return result is not null ? Results.Ok(result) : Results.Json(new { code = "mail_not_found", message = "Mail not found." }, statusCode: 404);
        });

        // GET /api/mail/{id}/recipients?page=1 — the full recipient list the caller may see, paged.
        g.MapGet("/{id:guid}/recipients", async (Guid id, int? page, int? pageSize,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.GetRecipientsAsync(userId.Value, id, page ?? 1, pageSize ?? 50, ct);
            return result is not null ? Results.Ok(result) : Results.Json(new { code = "mail_not_found", message = "Mail not found." }, statusCode: 404);
        });

        // ──────────────────────────────────────────────────────────────────────
        // Reading + delivery
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/mail/{id} — full detail (marks as read implicitly on the client's next call).
        g.MapGet("/{id:guid}", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var mail = await svc.GetAsync(userId.Value, id, ct);
            return mail is not null
                ? Results.Ok(mail)
                : Results.Json(new { code = "mail_not_found", message = "Mail not found." }, statusCode: 404);
        });

        // GET /api/mail/{id}/delivery — sender's delivery/read state per recipient.
        g.MapGet("/{id:guid}/delivery", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await svc.GetDeliveryStateAsync(userId.Value, id, ct));
        });

        // POST /api/mail/{id}/delivered — client hydration acknowledgment.
        g.MapPost("/{id:guid}/delivered", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            await svc.RecordDeliveredAsync(userId.Value, id, ct);
            return Results.Ok(new { ok = true });
        });

        // POST /api/mail/{id}/seen — read receipt from the reading pane (shown, tab visible + focused, 2 s).
        // Records SeenAt once for the caller's recipient row; the sender's own copy is ignored. Never
        // touches the unread badge (that stays POST /read). 404 when the caller is not on the mail.
        g.MapPost("/{id:guid}/seen", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await svc.RecordSeenAsync(userId.Value, id, ct);
            return r.Found
                ? Results.Ok(new { recorded = r.Recorded, seenAt = r.SeenAt })
                : Results.Json(new { code = "mail_not_found", message = "Mail not found." }, statusCode: 404);
        });

        // GET /api/mail/{id}/receipts — who has seen it: the sender and every To/Cc/Bcc recipient may ask.
        // Bcc rows only for the sender; mass mail / hidden list = counts only for non-senders.
        g.MapGet("/{id:guid}/receipts", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await svc.GetReceiptsAsync(userId.Value, id, ct);
            return r is not null ? Results.Ok(r) : Results.Json(new { code = "mail_not_found", message = "Mail not found." }, statusCode: 404);
        });

        // POST /api/mail/{id}/read — mark read.
        g.MapPost("/{id:guid}/read", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.MarkReadAsync(userId.Value, id, ct);
            return ResultOrError(result);
        });

        // POST /api/mail/{id}/unread — mark unread.
        g.MapPost("/{id:guid}/unread", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.MarkUnreadAsync(userId.Value, id, ct);
            return ResultOrError(result);
        });

        // POST /api/mail/{id}/star — toggle starred.
        g.MapPost("/{id:guid}/star", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.ToggleStarAsync(userId.Value, id, ct);
            return ResultOrError(result);
        });

        // POST /api/mail/{id}/pin — toggle pinned.
        g.MapPost("/{id:guid}/pin", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.TogglePinAsync(userId.Value, id, ct);
            return ResultOrError(result);
        });

        // POST /api/mail/{id}/cancel-schedule — cancel scheduled send back to draft.
        g.MapPost("/{id:guid}/cancel-schedule", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.CancelScheduleAsync(userId.Value, id, ct);
            if (result.Success) await WriteAuditAsync(http, db, "mail.schedule.cancel", id.ToString(), ct);
            return ResultOrError(result);
        }).RequirePermission("mail.send");

        // POST /api/mail/{id}/reschedule — change scheduled date/time.
        g.MapPost("/{id:guid}/reschedule", async (Guid id, RescheduleRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.RescheduleAsync(userId.Value, id, req.ScheduledAt, ct);
            if (result.Success) await WriteAuditAsync(http, db, "mail.schedule.reschedule", id.ToString(), ct);
            return ResultOrError(result);
        }).RequirePermission("mail.send");

        // POST /api/mail/{id}/send-now — bypass scheduled date and send immediately.
        g.MapPost("/{id:guid}/send-now", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.SendScheduledImmediatelyAsync(userId.Value, id, ct);
            if (result.Success) await WriteAuditAsync(http, db, "mail.schedule.sendnow", id.ToString(), ct);
            return ResultOrError(result);
        }).RequirePermission("mail.send");

        // ──────────────────────────────────────────────────────────────────────
        // Folder actions (single + bulk)
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/mail/{id}/move — { folder: "inbox|sent|draft|archive|trash" }
        g.MapPost("/{id:guid}/move", async (Guid id, MoveRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.MoveToFolderAsync(userId.Value, id, ParseFolder(req.Folder), ct);
            return ResultOrError(result);
        });

        // DELETE /api/mail/{id} — soft delete (trash) / permanent delete (from trash).
        g.MapDelete("/{id:guid}", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.DeleteAsync(userId.Value, id, ct);
            if (result.Success) await WriteAuditAsync(http, db, "mail.delete", id.ToString(), ct);
            return ResultOrError(result);
        });

        // POST /api/mail/empty-trash
        g.MapPost("/empty-trash", async (
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var count = await svc.EmptyTrashAsync(userId.Value, ct);
            await WriteAuditAsync(http, db, "mail.empty_trash", $"count={count}", ct);
            return Results.Ok(new { count });
        });

        // POST /api/mail/{id:guid}/mute
        g.MapPost("/{id:guid}/mute", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var muted = await svc.ToggleMuteAsync(userId.Value, id, ct);
            await WriteAuditAsync(http, db, "mail.mute", $"id={id};muted={muted}", ct);
            return Results.Ok(new { muted });
        });

        // POST /api/mail/bulk/move — { mailIds[], folder }
        g.MapPost("/bulk/move", async (BulkMoveRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.BulkMoveAsync(userId.Value, req.MailIds ?? [], ParseFolder(req.Folder), ct);
            return Results.Ok(result);
        }).RequirePermission("mail.send");

        // POST /api/mail/bulk/star — { mailIds[] }
        g.MapPost("/bulk/star", async (BulkIdsRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.BulkToggleStarAsync(userId.Value, req.MailIds ?? [], ct);
            return Results.Ok(result);
        });

        // POST /api/mail/bulk/delete — { mailIds[] }
        g.MapPost("/bulk/delete", async (BulkIdsRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.BulkDeleteAsync(userId.Value, req.MailIds ?? [], ct);
            return Results.Ok(result);
        });

        // POST /api/mail/bulk/restore — { mailIds[] } restores trashed messages to Inbox.
        g.MapPost("/bulk/restore", async (BulkIdsRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.BulkRestoreAsync(userId.Value, req.MailIds ?? [], ct);
            return Results.Ok(result);
        });

        // POST /api/mail/bulk/read — { folder } marks the whole folder read.
        g.MapPost("/bulk/read", async (FolderRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.MarkAllReadAsync(userId.Value, ParseFolder(req.Folder), ct);
            return Results.Ok(result);
        });

        // POST /api/mail/bulk/read-ids — { mailIds } marks the selected messages read in one call.
        g.MapPost("/bulk/read-ids", async (BulkIdsRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.BulkMarkReadAsync(userId.Value, req.MailIds ?? [], ct);
            return Results.Ok(result);
        });

        // ──────────────────────────────────────────────────────────────────────
        // PDF export (Fase 2 — render sungguhan via DevExpress Document Processing)
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/mail/{id}/export-pdf — mencatat permintaan ekspor (dipakai tombol Ekspor
        // di halaman baca) lalu mengembalikan PDF untuk diunduh.
        g.MapPost("/{id:guid}/export-pdf", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, IPop.Infrastructure.Documents.MailPdfRenderer pdf, IPop.Application.Attachments.IAttachmentStore store, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.RequestPdfExportAsync(userId.Value, id, ct);
            if (!result.Success) return ResultOrError(result);

            var models = await LoadMailPdfModels(userId.Value, [id], db, store, ct);
            if (models.Count == 0) return Results.Json(new { code = "mail_not_found", message = "Mail not found." }, statusCode: 404);
            byte[] bytes;
            try
            {
                bytes = await pdf.RenderMailAsync(models[0], ct);
            }
            catch (Exception ex)
            {
                var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("MailEndpoints");
                logger?.LogError(ex, "PDF export failed for mail {MailId}", id);
                return Results.Json(new { code = "pdf_render_failed", message = "Failed to render mail PDF." }, statusCode: 500);
            }
            return Results.File(bytes, "application/pdf", PdfFileName(models[0].Subject, id));
        });

        // GET /api/mail/{id}/export-pdf?mode=preview — pratinjau PDF inline untuk panel.
        g.MapGet("/{id:guid}/export-pdf", async (Guid id, string? mode,
            HttpContext http, IPopDbContext db, IPop.Infrastructure.Documents.MailPdfRenderer pdf, IPop.Application.Attachments.IAttachmentStore store, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var models = await LoadMailPdfModels(userId.Value, [id], db, store, ct);
            if (models.Count == 0) return Results.Json(new { code = "mail_not_found", message = "Mail not found." }, statusCode: 404);
            byte[] bytes;
            try
            {
                bytes = await pdf.RenderMailAsync(models[0], ct);
            }
            catch (Exception ex)
            {
                var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("MailEndpoints");
                logger?.LogError(ex, "PDF preview failed for mail {MailId}: {Error}", id, ex.Message);
                return Results.Json(new { code = "pdf_render_failed", message = "Failed to render mail PDF." }, statusCode: 500);
            }
            // mode=preview → inline (iframe); default → unduhan dengan nama dari subjek.
            var download = !string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase);
            return Results.File(bytes, "application/pdf",
                download ? PdfFileName(models[0].Subject, id) : null);
        });

        // POST /api/mail/export-pdf { ids: [...] } — satu PDF gabungan dari banyak surat.
        g.MapPost("/export-pdf", async (BulkExportRequest req,
            HttpContext http, IPopDbContext db, IPop.Infrastructure.Documents.MailPdfRenderer pdf, IPop.Application.Attachments.IAttachmentStore store, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var ids = req.Ids?.Distinct().Take(50).ToList() ?? [];
            if (ids.Count == 0)
                return Results.Json(new { code = "ids_required", message = "Select at least one email." }, statusCode: 422);

            var models = await LoadMailPdfModels(userId.Value, ids, db, store, ct);
            if (models.Count == 0)
                return Results.Json(new { code = "mail_not_found", message = "No emails accessible." }, statusCode: 404);

            byte[] bytes;
            try
            {
                bytes = models.Count == 1
                    ? await pdf.RenderMailAsync(models[0], ct)
                    : await pdf.RenderMergedAsync(models, ct);
            }
            catch (Exception ex)
            {
                var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("MailEndpoints");
                logger?.LogError(ex, "Bulk PDF export failed for user {UserId}: {Error}", userId.Value, ex.Message);
                return Results.Json(new { code = "export_failed", message = "Failed to export merged PDF." }, statusCode: 500);
            }
            return Results.File(bytes, "application/pdf", $"mail-merged-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf");
        });

        // ──────────────────────────────────────────────────────────────────────
        // Attachments — upload (orphan until linked by a send/draft), download, thumbnail
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/mail/attachments/upload — multipart: file(s). The web client sends one file
        // per request (with progress); up to MaxUploadFiles in one request still works.
        g.MapPost("/attachments/upload", async (HttpContext http,
            IPopDbContext db, IFileAccessService fileSvc, IWebHostEnvironment env, IConfiguration cfg, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            if (http.Request.ContentLength is > MaxUploadRequestBytes)
                return Results.Json(new { code = "upload_too_large", message = UploadTooLargeMessage }, statusCode: 413);
            if (!http.Request.HasFormContentType)
                return Results.Json(new { code = "file_required", message = "At least one file is required." }, statusCode: 422);

            // The default form limits (and Kestrel's ~28.6 MB body cap) are below the advertised
            // limit; the RequestSizeLimit metadata lifts the body cap, this lifts the form's.
            // The form reader spools files over 64 KB to disk, so big files are never held in memory.
            http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions
            {
                MultipartBodyLengthLimit = MaxUploadRequestBytes,
            }));
            IFormCollection form;
            try { form = await http.Request.ReadFormAsync(ct); }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
            {
                return Results.Json(new { code = "upload_too_large", message = UploadTooLargeMessage }, statusCode: 413);
            }
            var files = form.Files;
            if (files.Count == 0)
                return Results.Json(new { code = "file_required", message = "At least one file is required." }, statusCode: 422);
            if (files.Count > MaxUploadFiles)
                return Results.Json(new { code = "too_many_attachments", message = $"Maximum {MaxUploadFiles} files per upload." }, statusCode: 422);

            foreach (var file in files)
                if (file.Length > MaxUploadFileBytes)
                    return Results.Json(new { code = "file_too_large", message = FileTooLargeMessage(file.FileName) }, statusCode: 422);

            if (MailUploadFolder(cfg, userId.Value) is not { } mailPath)
                return Results.Json(new { code = "invalid_mail_folder", message = "MailFolder must be an absolute shared folder, not /home." }, statusCode: 503);

            var attachments = new List<AttachmentDto>();
            foreach (var file in files)
            {
                await using var src = file.OpenReadStream();
                attachments.Add(await StoreMailUploadAsync(http, db, fileSvc, env, userId.Value, mailPath, file.FileName, file.Length, src, ct));
            }
            await db.SaveChangesAsync(ct);

            return Results.Ok(attachments);
        }).RequirePermission("mail.send").DisableAntiforgery()
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadRequestBytes));

        // POST /api/mail/attachments/from-nas — { server, paths[] }: copies files the user can open
        // on a NAS into the mail folder server-side, so large files never pass through the browser.
        g.MapPost("/attachments/from-nas", async (NasAttachRequest req, HttpContext http,
            IPopDbContext db, IFileAccessService fileSvc, IWebHostEnvironment env, IConfiguration cfg, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var paths = (req.Paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct().ToList();
            if (string.IsNullOrWhiteSpace(req.Server) || paths.Count == 0)
                return Results.Json(new { code = "file_required", message = "At least one file is required." }, statusCode: 422);
            if (paths.Count > MaxUploadFiles)
                return Results.Json(new { code = "too_many_attachments", message = $"Maximum {MaxUploadFiles} files per upload." }, statusCode: 422);
            if (MailUploadFolder(cfg, userId.Value) is not { } mailPath)
                return Results.Json(new { code = "invalid_mail_folder", message = "MailFolder must be an absolute shared folder, not /home." }, statusCode: 503);

            var attachments = new List<AttachmentDto>();
            try
            {
                foreach (var path in paths)
                {
                    var name = Path.GetFileName(path);
                    await using var src = await fileSvc.DownloadAsync(userId.Value, req.Server.Trim(), path, ct);
                    var length = src.Length - src.Position;
                    if (length > MaxUploadFileBytes)
                        return Results.Json(new { code = "file_too_large", message = FileTooLargeMessage(name) }, statusCode: 422);
                    if (length <= 0)
                        return Results.Json(new { code = "file_empty", message = $"File '{name}' is empty." }, statusCode: 422);
                    attachments.Add(await StoreMailUploadAsync(http, db, fileSvc, env, userId.Value, mailPath, name, length, src, ct));
                }
            }
            catch (SynoException ex) when (ex.IsAuthFailure)
            {
                return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401);
            }
            catch (SynoException ex)
            {
                return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502);
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(attachments);
        }).RequirePermission("mail.send");

        // DELETE /api/mail/attachments/{id} — hapus lampiran (hanya orphan/pending milik
        // uploader; lampiran yang sudah terkirim di surat tidak bisa dihapus lewat sini).
        g.MapDelete("/attachments/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var attachment = await db.MailAttachments.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (attachment is null) return Results.NotFound();

            // Orphan = belum ditautkan ke surat (MailId null). Hanya uploader yang boleh
            // menghapus orphan miliknya.
            if (!IsPendingUploadOf(attachment, userId.Value))
                return Results.Json(new { code = "forbidden", message = "Only draft attachments belonging to you can be deleted." }, statusCode: 403);

            db.MailAttachments.Remove(attachment);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { success = true });
        }).RequirePermission("mail.send");

        // GET /api/mail/attachments/{id}/download?inline=true
        g.MapGet("/attachments/{id:guid}/download", async (Guid id, bool? inline,
            HttpContext http, IPopDbContext db, IWebHostEnvironment env, IFileAccessService fileSvc,
            IPop.Infrastructure.Files.AttachmentFileCache cache, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var attachment = await db.MailAttachments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id, ct);
            if (attachment is null) return Results.NotFound();

            // Access rule: only participants of the linked mail — or the uploader for pending orphans.
            var canAccess = attachment.MailId is { } linkedMailId
                ? await db.MailRecipients.AnyAsync(r => r.MailId == linkedMailId && r.UserId == userId.Value, ct)
                : IsPendingUploadOf(attachment, userId.Value);
            if (!canAccess) return Results.Forbid();

            if (attachment.SynologyServer == "local")
            {
                var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, "mail-files"));
                var combined = Path.GetFullPath(Path.Combine(env.ContentRootPath, attachment.SynologyPath));
                if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(combined))
                    return Results.NotFound();
                var stream = File.OpenRead(combined);
                return ServeAttachment(http, stream, attachment, inline);
            }
            try
            {
                // Legacy uploads remain readable with the uploader's NAS credential; new
                // uploads use the shared service account after mail-level authorization.
                var asService = IsSharedMailPath(attachment.SynologyPath) && fileSvc.HasServiceAccount
                    && attachment.SynologyServer == fileSvc.ServiceServer;
                Task<Stream> Download(CancellationToken c) => asService
                    ? fileSvc.DownloadAsServiceAsync(attachment.SynologyPath, c)
                    : fileSvc.DownloadAsync(userId.Value, attachment.SynologyServer, attachment.SynologyPath, c);
                // Big files are served from one local copy, so a viewer's range requests do not each pull the whole
                // file from the NAS again. Access is checked above on every request; the copy is keyed by attachment.
                if (attachment.FileSize > IPop.Infrastructure.Files.HttpSynologyGateway.InMemoryDownloadBytes)
                {
                    var local = await cache.GetAsync($"mail-{attachment.Id:N}", Download, ct);
                    return ServeAttachment(http, File.OpenRead(local), attachment, inline);
                }
                var stream = await Download(ct);
                return ServeAttachment(http, stream, attachment, inline);
            }
            catch (SynoException ex) when (ex.IsAuthFailure)
            {
                return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401);
            }
            catch (SynoException ex)
            {
                return SynoUnavailable(http, ex, id);
            }
        });

        // GET /api/mail/attachments/{id}/thumbnail
        g.MapGet("/attachments/{id:guid}/thumbnail", async (Guid id,
            HttpContext http, IPopDbContext db, IWebHostEnvironment env, IFileAccessService fileSvc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var attachment = await db.MailAttachments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id && a.ThumbnailPath != null, ct);
            if (attachment is null) return Results.NotFound();
            var canAccess = attachment.MailId is { } linkedMailId
                ? await db.MailRecipients.AnyAsync(r => r.MailId == linkedMailId && r.UserId == userId.Value, ct)
                : IsPendingUploadOf(attachment, userId.Value);
            if (!canAccess) return Results.Forbid();
            if (attachment.SynologyServer != "local")
            {
                try
                {
                    var stream = IsSharedMailPath(attachment.ThumbnailPath!) && fileSvc.HasServiceAccount
                        && attachment.SynologyServer == fileSvc.ServiceServer
                        ? await fileSvc.DownloadAsServiceAsync(attachment.ThumbnailPath!, ct)
                        : await fileSvc.DownloadAsync(userId.Value, attachment.SynologyServer, attachment.ThumbnailPath!, ct);
                    HardenFileResponse(http);
                    return Results.File(stream, IsRasterImageMime(attachment.MimeType) ? attachment.MimeType : "application/octet-stream", enableRangeProcessing: true);
                }
                catch (SynoException ex) when (ex.IsAuthFailure)
                {
                    return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401);
                }
                catch (SynoException ex)
                {
                    return SynoUnavailable(http, ex, id);
                }
            }
            var combined = Path.GetFullPath(Path.Combine(env.ContentRootPath, attachment.ThumbnailPath!));
            var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, "mail-files"));
            if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(combined))
                return Results.NotFound();
            HardenFileResponse(http);
            return Results.File(File.OpenRead(combined), IsRasterImageMime(attachment.MimeType) ? attachment.MimeType : "application/octet-stream");
        });

        // ──────────────────────────────────────────────────────────────────────
        // Surat Lanjutan (Fase 3) — label, aturan, template, balasan otomatis
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/mail/labels — label milik pengguna + jumlah surat terlabel.
        g.MapGet("/labels", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var rows = await db.MailLabels.AsNoTracking()
                .Where(l => l.UserId == userId.Value)
                .OrderBy(l => l.Name)
                .Select(l => new MailLabelDto(l.Id, l.Name, l.Color, l.Assignments.Count))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // POST /api/mail/labels — buat label (nama unik per pengguna).
        g.MapPost("/labels", async (CreateMailLabelRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var name = req.Name?.Trim() ?? "";
            var color = req.Color?.Trim() ?? "#2e7d5b";
            if (name.Length == 0 || name.Length > 100)
                return Results.Json(new { code = "invalid_name", message = "Label name is required (max 100 characters)." }, statusCode: 422);
            if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$"))
                return Results.Json(new { code = "invalid_color", message = "Color must be hex (#RGB/#RRGGBB)." }, statusCode: 422);

            var exists = await db.MailLabels.AnyAsync(l => l.UserId == userId.Value && l.Name == name, ct);
            if (exists)
                return Results.Json(new { code = "duplicate", message = "A label with this name already exists." }, statusCode: 422);

            var label = new IPop.Core.Entities.MailLabel
            {
                Id = Guid.NewGuid(), UserId = userId.Value, Name = name, Color = color, CreatedAt = DateTime.UtcNow
            };
            db.MailLabels.Add(label);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MailLabelDto(label.Id, label.Name, label.Color, 0));
        });

        // DELETE /api/mail/labels/{id} — hapus label (ikut melepas penempelan).
        g.MapDelete("/labels/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var label = await db.MailLabels.FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId.Value, ct);
            if (label is null) return Results.Json(new { code = "not_found", message = "Label not found." }, statusCode: 404);
            db.MailLabels.Remove(label);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { success = true });
        });

        // ── Aturan surat ──

        // GET /api/mail/rules
        g.MapGet("/rules", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var rows = await db.MailRules.AsNoTracking()
                .Where(r => r.UserId == userId.Value)
                .OrderBy(r => r.Name)
                .Select(r => new MailRuleDto(r.Id, r.Name, r.SenderContains, r.Label.Name, r.IsEnabled))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // POST /api/mail/rules
        g.MapPost("/rules", async (CreateMailRuleRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var name = req.Name?.Trim() ?? "";
            var senderContains = req.SenderContains?.Trim() ?? "";
            if (name.Length == 0 || name.Length > 200)
                return Results.Json(new { code = "invalid_name", message = "Rule name is required." }, statusCode: 422);
            if (senderContains.Length == 0 || senderContains.Length > 200)
                return Results.Json(new { code = "invalid_condition", message = "Sender condition is required." }, statusCode: 422);
            var label = await db.MailLabels.FirstOrDefaultAsync(l => l.Id == req.LabelId && l.UserId == userId.Value, ct);
            if (label is null)
                return Results.Json(new { code = "label_not_found", message = "Label not found." }, statusCode: 404);

            var rule = new IPop.Core.Entities.MailRule
            {
                Id = Guid.NewGuid(), UserId = userId.Value, Name = name,
                SenderContains = senderContains, LabelId = label.Id, IsEnabled = true, CreatedAt = DateTime.UtcNow
            };
            db.MailRules.Add(rule);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MailRuleDto(rule.Id, rule.Name, rule.SenderContains, label.Name, rule.IsEnabled));
        });

        // PUT /api/mail/rules/{id} — ubah (nama/kondisi/label/enabled).
        g.MapPut("/rules/{id:guid}", async (Guid id, UpdateMailRuleRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var rule = await db.MailRules.Include(r => r.Label).FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId.Value, ct);
            if (rule is null) return Results.Json(new { code = "not_found", message = "Rule not found." }, statusCode: 404);

            if (req.Name is not null)
            {
                var name = req.Name.Trim();
                if (name.Length == 0 || name.Length > 200) return Results.Json(new { code = "invalid_name", message = "Invalid rule name." }, statusCode: 422);
                rule.Name = name;
            }
            if (req.SenderContains is not null)
            {
                var s = req.SenderContains.Trim();
                if (s.Length == 0 || s.Length > 200) return Results.Json(new { code = "invalid_condition", message = "Invalid condition." }, statusCode: 422);
                rule.SenderContains = s;
            }
            if (req.LabelId is { } lid)
            {
                var label = await db.MailLabels.FirstOrDefaultAsync(l => l.Id == lid && l.UserId == userId.Value, ct);
                if (label is null) return Results.Json(new { code = "label_not_found", message = "Label not found." }, statusCode: 404);
                rule.LabelId = lid;
            }
            if (req.IsEnabled is { } en) rule.IsEnabled = en;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MailRuleDto(rule.Id, rule.Name, rule.SenderContains, rule.Label.Name, rule.IsEnabled));
        });

        // DELETE /api/mail/rules/{id}
        g.MapDelete("/rules/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var rule = await db.MailRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId.Value, ct);
            if (rule is null) return Results.Json(new { code = "not_found", message = "Rule not found." }, statusCode: 404);
            db.MailRules.Remove(rule);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { success = true });
        });

        // ── Template surat ──

        // GET /api/mail/templates
        g.MapGet("/templates", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var rows = await db.MailTemplates.AsNoTracking()
                .Where(t => t.UserId == userId.Value)
                .OrderBy(t => t.Name)
                .Select(t => new MailTemplateDto(t.Id, t.Name, t.Subject, t.Body))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // POST /api/mail/templates
        g.MapPost("/templates", async (CreateMailTemplateRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var name = req.Name?.Trim() ?? "";
            var subject = req.Subject?.Trim() ?? "";
            if (name.Length == 0 || name.Length > 200)
                return Results.Json(new { code = "invalid_name", message = "Template name is required." }, statusCode: 422);
            if (subject.Length == 0 || subject.Length > 500)
                return Results.Json(new { code = "invalid_subject", message = "Subject is required." }, statusCode: 422);

            var exists = await db.MailTemplates.AnyAsync(t => t.UserId == userId.Value && t.Name == name, ct);
            if (exists)
                return Results.Json(new { code = "duplicate", message = "A template with this name already exists." }, statusCode: 422);

            var tpl = new IPop.Core.Entities.MailTemplate
            {
                Id = Guid.NewGuid(), UserId = userId.Value, Name = name, Subject = subject,
                Body = req.Body ?? "", CreatedAt = DateTime.UtcNow
            };
            db.MailTemplates.Add(tpl);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MailTemplateDto(tpl.Id, tpl.Name, tpl.Subject, tpl.Body));
        });

        // PUT /api/mail/templates/{id}
        g.MapPut("/templates/{id:guid}", async (Guid id, UpdateMailTemplateRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var tpl = await db.MailTemplates.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value, ct);
            if (tpl is null) return Results.Json(new { code = "not_found", message = "Template not found." }, statusCode: 404);

            if (req.Name is not null)
            {
                var name = req.Name.Trim();
                if (name.Length == 0 || name.Length > 200) return Results.Json(new { code = "invalid_name", message = "Invalid name." }, statusCode: 422);
                tpl.Name = name;
            }
            if (req.Subject is not null)
            {
                var subject = req.Subject.Trim();
                if (subject.Length == 0 || subject.Length > 500) return Results.Json(new { code = "invalid_subject", message = "Invalid subject." }, statusCode: 422);
                tpl.Subject = subject;
            }
            if (req.Body is not null) tpl.Body = req.Body;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MailTemplateDto(tpl.Id, tpl.Name, tpl.Subject, tpl.Body));
        });

        // DELETE /api/mail/templates/{id}
        g.MapDelete("/templates/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var tpl = await db.MailTemplates.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId.Value, ct);
            if (tpl is null) return Results.Json(new { code = "not_found", message = "Template not found." }, statusCode: 404);
            db.MailTemplates.Remove(tpl);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { success = true });
        });

        // POST /api/mail/{id}/recall — tarik kembali surat terkirim selama belum dibaca penerima.
        g.MapPost("/{id:guid}/recall", async (Guid id,
            HttpContext http, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var mail = await db.Mails.Include(m => m.Recipients).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (mail is null || mail.SenderId != userId.Value)
                return Results.Json(new { code = "not_found", message = "Mail not found." }, statusCode: 404);
            if (mail.Status != "sent")
                return Results.Json(new { code = "not_sent", message = "Only sent mail can be recalled." }, statusCode: 422);

            // Once ONE recipient has actually seen it, recall is refused (Outlook rule). SeenAt, not ReadAt: "Mark all
            // read" without opening must not block a recall, and marking a seen mail unread must not allow one.
            var anyRead = mail.Recipients.Any(r => r.RecipientType != "from" && r.SeenAt != null);
            if (anyRead)
                return Results.Json(new { code = "already_read", message = "Mail has already been read by recipient — cannot be recalled." }, statusCode: 422);

            var now = DateTime.UtcNow;
            var affected = mail.Recipients.Select(r => r.UserId).Distinct().ToList();
            // Every read path (detail, thread, attachments, restore) is gated on the recipient row,
            // so removing it is what actually withdraws the mail — trash would stay readable/restorable.
            // The sender's own "from" row stays: the recalled mail remains in their Sent folder.
            db.MailRecipients.RemoveRange(mail.Recipients.Where(r => r.RecipientType != "from" && r.UserId != mail.SenderId));
            mail.Status = "recalled";
            mail.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            // Recalled mail leaves the recipients' inboxes: resync their lists + badges, and the
            // sender's other tabs. Best-effort — a hub failure never fails the recall.
            try
            {
                var users = await db.Users.AsNoTracking().Where(u => affected.Contains(u.Id))
                    .Select(u => new { u.Id, u.Nik }).ToListAsync(ct);
                foreach (var u in users)
                {
                    var unread = await db.MailRecipients.AsNoTracking()
                        .CountAsync(r => r.UserId == u.Id && r.Folder == "inbox" && !r.IsRead && !r.IsMuted, ct);
                    await realtime.ToUserAsync(u.Nik, RealtimeEvents.BadgeChanged, new RealtimeArgs("mail", unread), ct);
                }
                await realtime.ToUsersAsync(users.Select(u => u.Nik), RealtimeEvents.MailChanged, ct: ct);
            }
            catch { /* realtime is a hint */ }
            return Results.Ok(new { success = true });
        });

        // POST /api/mail/{mailId}/labels/{labelId} — tempel label ke surat (idempotent).
        g.MapPost("/{mailId:guid}/labels/{labelId:guid}", async (Guid mailId, Guid labelId,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var label = await db.MailLabels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == labelId && l.UserId == userId.Value, ct);
            if (label is null) return Results.Json(new { code = "label_not_found", message = "Label not found." }, statusCode: 404);
            var canSee = await db.MailRecipients.AnyAsync(r => r.MailId == mailId && r.UserId == userId.Value, ct);
            if (!canSee) return Results.Json(new { code = "mail_not_found", message = "Mail not found." }, statusCode: 404);

            var exists = await db.MailLabelAssignments.AnyAsync(a => a.LabelId == labelId && a.MailId == mailId, ct);
            if (!exists)
            {
                db.MailLabelAssignments.Add(new IPop.Core.Entities.MailLabelAssignment
                {
                    Id = Guid.NewGuid(), LabelId = labelId, MailId = mailId, CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(new { success = true });
        });

        // DELETE /api/mail/{mailId}/labels/{labelId} — lepas label dari surat.
        g.MapDelete("/{mailId:guid}/labels/{labelId:guid}", async (Guid mailId, Guid labelId,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var label = await db.MailLabels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == labelId && l.UserId == userId.Value, ct);
            if (label is null) return Results.Json(new { code = "label_not_found", message = "Label not found." }, statusCode: 404);
            await db.MailLabelAssignments.Where(a => a.LabelId == labelId && a.MailId == mailId).ExecuteDeleteAsync(ct);
            return Results.Ok(new { success = true });
        });

        // ── Balasan otomatis (out of office) ──

        // GET /api/mail/auto-reply — pengaturan OOO pengguna saat ini.
        g.MapGet("/auto-reply", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var s = await db.MailAutoReplySettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId.Value, ct);
            return Results.Ok(s is null
                ? new MailAutoReplyDto(false, null, null, null)
                : new MailAutoReplyDto(s.IsEnabled, s.FromDate, s.UntilDate, s.Message));
        });

        // PUT /api/mail/auto-reply — simpan pengaturan OOO (upsert).
        g.MapPut("/auto-reply", async (UpdateAutoReplyRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (req.IsEnabled && string.IsNullOrWhiteSpace(req.Message))
                return Results.Json(new { code = "message_required", message = "Auto-reply message is required when enabled." }, statusCode: 422);
            if (req.Message is not null && req.Message.Length > 2000)
                return Results.Json(new { code = "message_too_long", message = "Message must not exceed 2000 characters." }, statusCode: 422);

            var s = await db.MailAutoReplySettings.FirstOrDefaultAsync(x => x.UserId == userId.Value, ct);
            if (s is null)
            {
                s = new IPop.Core.Entities.MailAutoReplySetting { Id = Guid.NewGuid(), UserId = userId.Value };
                db.MailAutoReplySettings.Add(s);
            }
            s.IsEnabled = req.IsEnabled;
            s.FromDate = req.FromDate;
            s.UntilDate = req.UntilDate;
            s.Message = req.Message?.Trim();
            s.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MailAutoReplyDto(s.IsEnabled, s.FromDate, s.UntilDate, s.Message));
        });

        return app;
    }

    /// Up to 5 files of 500 MB each per request, plus multipart framing.
    internal const int MaxUploadFiles = 5;
    internal const long MaxUploadFileBytes = 500L * 1024 * 1024;
    internal const long MaxUploadRequestBytes = MaxUploadFiles * MaxUploadFileBytes + 1024 * 1024;
    /// Send/draft/forward bodies carry pasted images as data: URIs until the server moves them to the NAS.
    internal const long MaxComposeRequestBytes = 128L * 1024 * 1024;
    private const string UploadTooLargeMessage = "The upload is too large. Attach at most 5 files of 500 MB each.";
    private static string FileTooLargeMessage(string? name) => $"File '{name}' exceeds the 500 MB limit.";

    public record NasAttachRequest(string? Server, IReadOnlyList<string>? Paths);

    /// The shared folder pending uploads go to; null when MailFolder is misconfigured.
    private static string? MailUploadFolder(IConfiguration cfg, Guid userId)
    {
        var mailRoot = cfg["Synology:MailFolder"]?.Trim().TrimEnd('/') ?? "/sja/SJA SPJ/ICT/mail";
        if (!mailRoot.StartsWith('/') || mailRoot == "/home" || mailRoot.StartsWith("/home/", StringComparison.OrdinalIgnoreCase))
            return null;
        return $"{mailRoot}/drafts/{userId:N}"; // pending uploads live under the composer; link later
    }

    /// Stores one pending upload (NAS via the service account, local disk as fallback) and adds
    /// its row to the change tracker; the caller saves. <paramref name="src"/> must be seekable
    /// (form files and NAS downloads are), so a failed NAS upload can fall back from the start.
    private static async Task<AttachmentDto> StoreMailUploadAsync(HttpContext http, IPopDbContext db, IFileAccessService fileSvc,
        IWebHostEnvironment env, Guid userId, string mailPath, string? fileName, long length, Stream src, CancellationToken ct)
    {
        var safeName = SanitizeFileName(fileName);
        var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
        var start = src.Position;
        var head = new byte[16];
        var headLen = await src.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        src.Position = start;
        var mime = ResolveMime(safeName, head.AsSpan(0, headLen));
        var isRaster = IsRasterImageMime(mime);
        string? thumbnailPath = null;

        string server = "local";
        string storedPath = "";
        var uploadedToSynology = false;

        if (fileSvc.HasServiceAccount && fileSvc.ServiceServer is { } serviceServer)
        {
            try
            {
                await UploadMailAsServiceAsync(fileSvc, mailPath, uniqueName, src, ct);
                server = serviceServer;
                storedPath = $"{mailPath}/{uniqueName}";
                thumbnailPath = isRaster ? storedPath : null;
                uploadedToSynology = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("MailEndpoints");
                logger?.LogWarning(ex, "Shared Synology mail upload failed at {Server}:{Path}, falling back to local storage",
                    serviceServer, $"{mailPath}/{uniqueName}");
            }
        }

        if (!uploadedToSynology)
        {
            var dir = Path.Combine(env.ContentRootPath, "mail-files", userId.ToString("N"));
            Directory.CreateDirectory(dir);
            var full = Path.Combine(dir, uniqueName);
            src.Position = start;
            await using (var fs = File.Create(full))
                await src.CopyToAsync(fs, ct);
            server = "local";
            storedPath = Path.Combine("mail-files", userId.ToString("N"), uniqueName).Replace('\\', '/');
            if (isRaster) thumbnailPath = storedPath;
        }

        var attachment = new MailAttachment
        {
            Id = Guid.NewGuid(),
            MailId = null, // pending until a send/draft links it
            UploaderId = userId,
            FileName = safeName,
            FileSize = length,
            MimeType = mime,
            SynologyServer = server,
            SynologyPath = storedPath,
            ThumbnailPath = thumbnailPath,
            CreatedAt = DateTime.UtcNow,
        };
        db.MailAttachments.Add(attachment);
        return new AttachmentDto(attachment.Id, attachment.FileName, attachment.FileSize, attachment.MimeType, attachment.ThumbnailPath);
    }

    /// A pending (unlinked) upload belongs to its uploader and nobody else.
    internal static bool IsPendingUploadOf(MailAttachment a, Guid userId) =>
        a.MailId is null && a.UploaderId == userId;

    internal static bool IsOwnMailDraftPath(string path, Guid userId) =>
        path.StartsWith($"mail-files/{userId:N}/", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"/drafts/{userId:N}/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSharedMailPath(string path) =>
        path.StartsWith('/') && !path.StartsWith("/home/", StringComparison.OrdinalIgnoreCase)
        && !path.Equals("/home", StringComparison.OrdinalIgnoreCase)
        && path.Contains("/drafts/", StringComparison.OrdinalIgnoreCase);

    internal static async Task UploadMailAsServiceAsync(IFileAccessService fileSvc,
        string directory, string fileName, Stream content, CancellationToken ct)
    {
        if (content.CanSeek) content.Position = 0;
        await fileSvc.UploadAsServiceAsync(directory, fileName, content, ct);
    }

    internal static async Task UploadToSynologyAsync(IFileAccessService fileSvc, Guid userId, string server,
        string directory, string fileName, Stream content, CancellationToken ct)
    {
        if (content.CanSeek) content.Position = 0;
        await fileSvc.UploadAsync(userId, server, directory, fileName, content, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik);
        return user?.Id;
    }

    private static async Task WriteAuditAsync(HttpContext http, IPopDbContext db, string action, string entityId, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = http.User.FindFirst("nik")?.Value,
            Action = action,
            Entity = "Mail",
            EntityId = entityId,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    /// Sends above MailLimits.AuditAbove recipients are logged for admins with the count.
    private static async Task AuditMassAsync(HttpContext http, IPopDbContext db, SendMailResult result, CancellationToken ct)
    {
        if (result.RecipientCount is not int n || n <= MailLimits.AuditAbove) return;
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = http.User.FindFirst("nik")?.Value,
            Action = "mail.send.mass",
            Entity = "Mail",
            EntityId = result.MailId?.ToString(),
            DetailJson = System.Text.Json.JsonSerializer.Serialize(new { recipients = n }),
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static IResult SendError(SendMailResult result)
        => Results.Json(new { code = result.Error, message = SendErrorMessage(result), recipientCount = result.RecipientCount },
            statusCode: ErrorStatus(result.Error));

    /// Human text for compose errors — the composer shows it as is.
    internal static string SendErrorMessage(SendMailResult result) => result.Error switch
    {
        "too_many_recipients" => result.RecipientCount is int n
            ? $"This mail would go to {n:N0} people. The limit is {MailLimits.MaxRecipients:N0} — remove some recipients or split it into two mails."
            : $"Too many recipients. The limit is {MailLimits.MaxRecipients:N0} people per mail.",
        "too_many_groups" => "Too many groups in one mail. Pick at most 100.",
        "confirm_required" => $"Please confirm sending to {result.RecipientCount:N0} people.",
        "recipient_not_found" => "One or more recipients don't exist or are no longer active. Remove them and try again.",
        "group_not_found" => "One of the groups no longer exists. Remove it and try again.",
        "reply_all_blocked" => "This mail went to many people. Your reply can only go to the sender.",
        "no_recipients" => "Add at least one recipient.",
        "subject_required" => "Add a subject.",
        "subject_too_long" => "The subject is too long.",
        "body_required" => "Write a message.",
        "schedule_in_past" => "The scheduled time has already passed. Pick a time in the future.",
        "schedule_too_far" => "Mail can be scheduled at most a year ahead.",
        "already_sent" => "This mail was already sent at its scheduled time.",
        _ => result.Error ?? "Couldn't send the mail.",
    };

    private static IResult SynoUnavailable(HttpContext http, SynoException ex, Guid attachmentId)
    {
        http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("MailEndpoints")
            .LogWarning(ex, "Mail attachment {AttachmentId}: file storage error", attachmentId);
        return Results.Json(new { code = "synology_error", message = "File storage is unavailable. Try again shortly." }, statusCode: 502);
    }

    private static IResult ResultOrError(SendMailResult result)
        => result.Success
            ? Results.Ok(result)
            : Results.Json(new { code = result.Error, message = SendErrorMessage(result) }, statusCode: ErrorStatus(result.Error));

    private static int ErrorStatus(string? error) => error switch
    {
        var e when e?.Contains("not_found") == true => 404,
        var e when e?.Contains("forbidden") == true => 403,
        var e when e?.Contains("trashed") == true => 409,
        "already_sent" => 409,
        _ => 422
    };

    private static MailFolder ParseFolder(string? folder) => folder?.ToLowerInvariant() switch
    {
        "inbox" => MailFolder.Inbox,
        "sent" => MailFolder.Sent,
        "draft" => MailFolder.Draft,
        "starred" => MailFolder.Starred,
        "archive" => MailFolder.Archive,
        "trash" => MailFolder.Trash,
        _ => MailFolder.Inbox
    };

    private static bool IsRasterImageMime(string? contentType) => contentType?.ToLowerInvariant() is
        "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/bmp" or "image/tiff";

    private static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider MimeByExt = new();

    /// The type comes from the server, never from the browser's Content-Type header (which the
    /// uploader controls): by extension, and for the types we show inline the file's own bytes
    /// must agree. Anything unknown or contradicting its bytes is a plain download.
    internal static string ResolveMime(string name, ReadOnlySpan<byte> head)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var mime = ext switch
        {
            ".doc" => "application/msword",
            ".xls" => "application/vnd.ms-excel",
            _ => MimeByExt.TryGetContentType(name, out var m) ? m : "application/octet-stream",
        };
        if (mime.StartsWith("image/", StringComparison.Ordinal) || mime == "application/pdf")
        {
            var sniffed = SniffMime(head);
            // An "x.png" that is really HTML/SVG/script must not be stored or served as an image.
            if (sniffed is null || (mime != sniffed && !(mime.StartsWith("image/") && sniffed.StartsWith("image/"))))
                return mime == "image/svg+xml" ? "application/octet-stream" : (sniffed ?? "application/octet-stream");
            return sniffed;
        }
        return mime;
    }

    /// Magic numbers of the formats the client previews inline.
    internal static string? SniffMime(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a') return "image/gif";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M') return "image/bmp";
        if (b.Length >= 4 && ((b[0] == 'I' && b[1] == 'I' && b[2] == 42 && b[3] == 0) || (b[0] == 'M' && b[1] == 'M' && b[2] == 0 && b[3] == 42))) return "image/tiff";
        if (b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F' && b[4] == '-') return "application/pdf";
        return null;
    }

    /// Types the browser may render in place; everything else is always an attachment download.
    internal static bool IsSafeInlineMime(string? mime) => IsRasterImageMime(mime)
        || mime is "application/pdf" or "text/plain" or "text/csv";

    /// File responses never run as a page of ours: no sniffing, and a sandbox CSP in case one
    /// is ever opened directly in a tab.
    private static void HardenFileResponse(HttpContext http)
    {
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'";
    }

    private static IResult ServeAttachment(HttpContext http, Stream stream, MailAttachment a, bool? inline)
    {
        HardenFileResponse(http);
        // inline=true is honoured only for safe types; e.g. text/html always downloads.
        var asInline = inline == true && IsSafeInlineMime(a.MimeType);
        var type = asInline ? a.MimeType : (IsSafeInlineMime(a.MimeType) ? a.MimeType : "application/octet-stream");
        return Results.File(stream, type, asInline ? null : a.FileName, enableRangeProcessing: true);
    }

    private static string SanitizeFileName(string? name)
    {
        var raw = Path.GetFileName(name ?? "");
        if (string.IsNullOrWhiteSpace(raw)) return "file";
        var cleaned = string.Concat(raw.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return cleaned.Length > 180 ? cleaned[..180] : cleaned;
    }

    // Request DTOs
    public record MoveRequest(string? Folder);
    public record BulkMoveRequest(IReadOnlyList<Guid> MailIds, string? Folder);
    public record BulkIdsRequest(IReadOnlyList<Guid> MailIds);
    public record FolderRequest(string? Folder);
    public record RescheduleRequest(DateTime ScheduledAt);
    public record BulkExportRequest(IReadOnlyList<Guid>? Ids);
    // Surat Lanjutan (Fase 3)
    public record CreateMailLabelRequest(string? Name, string? Color);
    public record MailLabelDto(Guid Id, string Name, string Color, int Count);
    public record CreateMailRuleRequest(string? Name, string? SenderContains, Guid LabelId);
    public record UpdateMailRuleRequest(string? Name, string? SenderContains, Guid? LabelId, bool? IsEnabled);
    public record MailRuleDto(Guid Id, string Name, string SenderContains, string LabelName, bool IsEnabled);
    public record CreateMailTemplateRequest(string? Name, string? Subject, string? Body);
    public record UpdateMailTemplateRequest(string? Name, string? Subject, string? Body);
    public record MailTemplateDto(Guid Id, string Name, string Subject, string Body);
    // Balasan otomatis (Fase 3)
    public record UpdateAutoReplyRequest(bool IsEnabled, DateTime? FromDate, DateTime? UntilDate, string? Message);
    public record MailAutoReplyDto(bool IsEnabled, DateTime? FromDate, DateTime? UntilDate, string? Message);

    // ── PDF render helpers (Fase 2) ──

    /// Muat surat yang bisa diakses pengguna dan petakan ke model render. Pasted images stored on
    /// the NAS go back in as data: URIs — the renderer cannot fetch /api/attachments with a token.
    private static async Task<List<IPop.Infrastructure.Documents.MailPdfRenderer.MailPdfModel>> LoadMailPdfModels(
        Guid userId, IReadOnlyList<Guid> ids, IPopDbContext db, IPop.Application.Attachments.IAttachmentStore store, CancellationToken ct)
    {
        var rows = await db.Mails.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .Where(m => m.Recipients.Any(r => r.UserId == userId && r.Folder != "pending"))
            .Include(m => m.Sender)
            .Include(m => m.Recipients).ThenInclude(r => r.User)
            .OrderBy(m => m.SentAt ?? m.CreatedAt)
            .ToListAsync(ct);

        foreach (var m in rows.Where(m => m.IsHtml))
            m.Body = await IPop.Infrastructure.Mail.MailInlineImages.InlineForExportAsync(db, store, m.Id, m.Body, ct);

        return rows.Select(m => new IPop.Infrastructure.Documents.MailPdfRenderer.MailPdfModel(
            m.Subject,
            m.Body ?? "",
            m.IsHtml,
            m.Sender?.Name ?? "Unknown",
            m.Sender?.Nik ?? "",
            string.Join("; ", PdfVisibleRecipients(m, userId).Select(r => r.User?.Name ?? "Unknown")),
            m.Signature,
            m.Importance,
            m.SentAt
        )).ToList();
    }

    /// The same privacy rules as the reading pane (MailService.VisibleRecipients + group summary):
    /// the PDF used to print every row, so any recipient could export the BCC list, a hidden
    /// recipient list or the members of a group.
    private static IEnumerable<IPop.Core.Entities.MailRecipient> PdfVisibleRecipients(IPop.Core.Entities.Mail m, Guid userId)
    {
        var rows = m.Recipients.Where(r => r.RecipientType != "from");
        if (m.SenderId == userId) return rows;
        if (m.HideRecipients) return rows.Where(r => r.UserId == userId);
        return rows.Where(r => r.UserId == userId || (r.RecipientType != "bcc" && !r.ViaGroup));
    }

    private static async Task<byte[]?> RenderMailPdf(Guid userId, Guid mailId,
        IPop.Infrastructure.Documents.MailPdfRenderer pdf, IPopDbContext db, IPop.Application.Attachments.IAttachmentStore store, CancellationToken ct)
    {
        var models = await LoadMailPdfModels(userId, [mailId], db, store, ct);
        if (models.Count == 0) return null;
        return await pdf.RenderMailAsync(models[0], ct);
    }

    /// Nama berkas unduhan dari subjek surat: bersih, terbatas, tetap bermakna.
    private static string PdfFileName(string? subject, Guid fallbackId)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return $"mail-{fallbackId.ToString("N")[..8]}.pdf";
        var cleaned = string.Concat(subject.Trim().Select(c =>
            char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : ' '));
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", "-").Trim('-');
        if (cleaned.Length > 60) cleaned = cleaned[..60].Trim('-');
        return (cleaned.Length == 0 ? $"mail-{fallbackId.ToString("N")[..8]}" : cleaned.ToLowerInvariant()) + ".pdf";
    }
}
