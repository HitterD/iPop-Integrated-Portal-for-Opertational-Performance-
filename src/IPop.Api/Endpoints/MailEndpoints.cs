using IPop.Api.Rbac;
using IPop.Application.Files;
using IPop.Application.Mail;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
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
        g.MapGet("/folders/{folder}", async (string folder, int? page, int? pageSize, string? q, bool? starredOnly, bool? unreadOnly, string? senderNik,
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
            var search = new MailSearchRequest(q, starredOnly, unreadOnly, senderNik);
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
            if (!result.Success)
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: ErrorStatus(result.Error));
            await WriteAuditAsync(http, db, "mail.send", result.MailId!.Value.ToString(), ct);
            return Results.Ok(result);
        }).RequirePermission("mail.send");

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
        }).RequirePermission("mail.send");

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
        }).RequirePermission("mail.send");

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
            if (!result.Success)
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: ErrorStatus(result.Error));
            await WriteAuditAsync(http, db, "mail.forward", id.ToString(), ct);
            return Results.Ok(result);
        }).RequirePermission("mail.send");

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

        // POST /api/mail/bulk/move — { mailIds[], folder }
        g.MapPost("/bulk/move", async (BulkMoveRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.BulkMoveAsync(userId.Value, req.MailIds, ParseFolder(req.Folder), ct);
            return Results.Ok(result);
        }).RequirePermission("mail.send");

        // POST /api/mail/bulk/star — { mailIds[] }
        g.MapPost("/bulk/star", async (BulkIdsRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.BulkToggleStarAsync(userId.Value, req.MailIds, ct);
            return Results.Ok(result);
        });

        // POST /api/mail/bulk/delete — { mailIds[] }
        g.MapPost("/bulk/delete", async (BulkIdsRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.BulkDeleteAsync(userId.Value, req.MailIds, ct);
            return Results.Ok(result);
        });

        // POST /api/mail/bulk/restore — { mailIds[] } restores trashed messages to Inbox.
        g.MapPost("/bulk/restore", async (BulkIdsRequest req,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.BulkRestoreAsync(userId.Value, req.MailIds, ct);
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

        // ──────────────────────────────────────────────────────────────────────
        // Signature/paraf + export stub
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/mail/{id}/export-pdf — records the request; real PDF is Wave-2.
        g.MapPost("/{id:guid}/export-pdf", async (Guid id,
            HttpContext http, IMailService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.RequestPdfExportAsync(userId.Value, id, ct);
            return ResultOrError(result);
        });

        // ──────────────────────────────────────────────────────────────────────
        // Attachments — upload (orphan until linked by a send/draft), download, thumbnail
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/mail/attachments/upload — multipart: file(s)
        g.MapPost("/attachments/upload", async (HttpContext http,
            IPopDbContext db, IFileAccessService fileSvc, IWebHostEnvironment env, IConfiguration cfg, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var form = await http.Request.ReadFormAsync(ct);
            var files = form.Files;
            if (files.Count == 0)
                return Results.Json(new { code = "file_required", message = "At least one file is required." }, statusCode: 422);
            if (files.Count > 5)
                return Results.Json(new { code = "too_many_attachments", message = "Maximum 5 files per upload." }, statusCode: 422);

            const long maxFileSize = 50L * 1024 * 1024; // 50MB
            foreach (var file in files)
                if (file.Length > maxFileSize)
                    return Results.Json(new { code = "file_too_large", message = $"File '{file.FileName}' exceeds the 50MB limit." }, statusCode: 422);

            var cred = await db.SynologyCredentials.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserId == userId.Value, ct);

            var attachments = new List<AttachmentDto>();
            var mailRoot = cfg["Synology:MailFolder"]?.Trim().TrimEnd('/') ?? "/home/mail";
            if (!mailRoot.StartsWith('/')) mailRoot = "/" + mailRoot;
            var mailPath = $"{mailRoot}/drafts/{userId:N}"; // pending uploads live under the composer; link later

            foreach (var file in files)
            {
                var safeName = SanitizeFileName(file.FileName);
                var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
                var mime = ResolveMime(safeName, file.ContentType);
                var isRaster = IsRasterImageMime(mime);
                string? thumbnailPath = null;

                string server = "local";
                string storedPath = "";
                var uploadedToSynology = false;

                if (cred is not null && !cred.IsInvalid)
                {
                    try
                    {
                        await using var buffered = new MemoryStream();
                        await using (var src = file.OpenReadStream())
                            await src.CopyToAsync(buffered, ct);
                        buffered.Position = 0;

                        // Mail attachments go through the same Synology file flow as chat (server's file service).
                        await fileSvc.UploadAsync(userId.Value, cred.Server, mailPath, uniqueName, buffered, ct);
                        server = cred.Server;
                        storedPath = $"{mailPath}/{uniqueName}";
                        thumbnailPath = isRaster ? storedPath : null;
                        uploadedToSynology = true;
                    }
                    catch (Exception ex)
                    {
                        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("MailEndpoints");
                        logger?.LogWarning(ex, "Synology mail upload failed for user {UserId} to {Server}:{Path}, falling back to local storage: {Error}",
                            userId.Value, cred.Server, $"{mailPath}/{uniqueName}", ex.Message);
                        uploadedToSynology = false;
                    }
                }

                if (!uploadedToSynology)
                {
                    var dir = Path.Combine(env.ContentRootPath, "mail-files", userId.Value.ToString("N"));
                    Directory.CreateDirectory(dir);
                    var full = Path.Combine(dir, uniqueName);
                    await using (var fs = File.Create(full))
                        await using (var src = file.OpenReadStream())
                            await src.CopyToAsync(fs, ct);
                    server = "local";
                    storedPath = Path.Combine("mail-files", userId.Value.ToString("N"), uniqueName).Replace('\\', '/');
                    if (isRaster) thumbnailPath = storedPath;
                }

                var attachment = new MailAttachment
                {
                    Id = Guid.NewGuid(),
                    MailId = Guid.Empty, // orphan until linked on send/draft
                    FileName = safeName,
                    FileSize = file.Length,
                    MimeType = mime,
                    SynologyServer = server,
                    SynologyPath = storedPath,
                    ThumbnailPath = thumbnailPath,
                    CreatedAt = DateTime.UtcNow,
                };
                db.MailAttachments.Add(attachment);
                attachments.Add(new AttachmentDto(attachment.Id, attachment.FileName, attachment.FileSize, attachment.MimeType, attachment.ThumbnailPath));
            }
            await db.SaveChangesAsync(ct);

            return Results.Ok(attachments);
        }).RequirePermission("mail.send").DisableAntiforgery();

        // GET /api/mail/attachments/{id}/download?inline=true
        g.MapGet("/attachments/{id:guid}/download", async (Guid id, bool? inline,
            HttpContext http, IPopDbContext db, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var attachment = await db.MailAttachments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id, ct);
            if (attachment is null) return Results.NotFound();

            // Access rule: only participants of the linked mail — or the uploader for pending orphans
            // (path encodes the uploader's user id).
            var canAccess = attachment.MailId != Guid.Empty
                ? await db.MailRecipients.AnyAsync(r => r.MailId == attachment.MailId && r.UserId == userId.Value, ct)
                : attachment.SynologyPath.StartsWith($"mail-files/{userId.Value:N}", StringComparison.OrdinalIgnoreCase);
            if (!canAccess) return Results.Forbid();

            if (attachment.SynologyServer == "local")
            {
                var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, "mail-files"));
                var combined = Path.GetFullPath(Path.Combine(env.ContentRootPath, attachment.SynologyPath));
                if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(combined))
                    return Results.NotFound();
                var stream = File.OpenRead(combined);
                return Results.File(stream, attachment.MimeType, inline == true ? null : attachment.FileName, enableRangeProcessing: true);
            }
            // Synology: the File service's download endpoint serves the file at SynologyPath.
            return Results.Redirect($"/api/files/download?server={Uri.EscapeDataString(attachment.SynologyServer)}&path={Uri.EscapeDataString(attachment.SynologyPath)}&inline={inline == true}");
        });

        // GET /api/mail/attachments/{id}/thumbnail
        g.MapGet("/attachments/{id:guid}/thumbnail", async (Guid id,
            HttpContext http, IPopDbContext db, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var attachment = await db.MailAttachments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == id && a.ThumbnailPath != null, ct);
            if (attachment?.SynologyServer != "local")
                return Results.Redirect($"/api/files/download?server={Uri.EscapeDataString(attachment?.SynologyServer ?? "")}&path={Uri.EscapeDataString(attachment?.ThumbnailPath ?? "")}&inline=true");
            var combined = Path.GetFullPath(Path.Combine(env.ContentRootPath, attachment.ThumbnailPath!));
            var root = Path.GetFullPath(Path.Combine(env.ContentRootPath, "mail-files"));
            if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(combined))
                return Results.NotFound();
            return Results.File(File.OpenRead(combined), "image/jpeg");
        });

        return app;
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

    private static IResult ResultOrError(SendMailResult result)
        => result.Success
            ? Results.Ok(result)
            : Results.Json(new { code = result.Error, message = result.Error }, statusCode: ErrorStatus(result.Error));

    private static int ErrorStatus(string? error) => error switch
    {
        var e when e?.Contains("not_found") == true => 404,
        var e when e?.Contains("forbidden") == true => 403,
        var e when e?.Contains("trashed") == true => 409,
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

    private static string ResolveMime(string name, string? contentType)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".doc" or ".docx" => "application/msword",
            ".xls" or ".xlsx" => "application/vnd.ms-excel",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            _ => !string.IsNullOrWhiteSpace(contentType) ? contentType! : "application/octet-stream"
        };
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
}
