using IPop.Api.Rbac;
using IPop.Application.Chat;
using IPop.Application.Files;
using IPop.Application.Rbac;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace IPop.Api.Endpoints;

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat").RequireAuthorization().RequirePermission("chat.read");

        // ──────────────────────────────────────────────────────────────────────
        // Rooms
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/chat/rooms?tab=direct|groups&page=1&pageSize=20
        g.MapGet("/rooms", async (string? tab, int? page, int? pageSize,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var roomTab = tab?.ToLowerInvariant() switch
            {
                "groups" => RoomTab.Groups,
                _ => RoomTab.Direct
            };
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);

            var result = await svc.GetRoomsAsync(userId.Value, roomTab, p, size, ct);
            return Results.Ok(result);
        });

        // GET /api/chat/rooms/{roomId}
        g.MapGet("/rooms/{roomId:guid}", async (Guid roomId,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var room = await svc.GetRoomAsync(roomId, userId.Value, ct);
            return room is not null
                ? Results.Ok(room)
                : Results.Json(new { code = "room_not_found", message = "Room does not exist or you are not a member." }, statusCode: 404);
        });

        // POST /api/chat/rooms/dm — { recipientId }
        g.MapPost("/rooms/dm", async (CreateDmRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var room = await svc.CreateDirectRoomAsync(userId.Value, req.RecipientId, ct);
            await notifier.NotifyAddedAsync(room.Members.Select(m => m.UserId), room.Id, ct);
            return Results.Ok(room);
        }).RequirePermission("chat.send");

        // POST /api/chat/rooms/group — { name, memberIds[] }
        g.MapPost("/rooms/group", async (CreateGroupRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            try
            {
                var room = await svc.CreateGroupRoomAsync(userId.Value, req, ct);
                await notifier.NotifyAddedAsync(room.Members.Select(m => m.UserId), room.Id, ct);
                return Results.Ok(room);
            }
            catch (InvalidOperationException ex) when (ex.Message is "group_too_small" or "group_full")
            {
                return Results.Json(new { code = ex.Message, message = ex.Message }, statusCode: 422);
            }
        }).RequirePermission("chat.send");

        // DELETE /api/chat/rooms/{roomId}/leave
        g.MapDelete("/rooms/{roomId:guid}/leave", async (Guid roomId,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.LeaveGroupAsync(userId.Value, roomId, ct);
            if (!result.Success)
                return Results.Json(new { code = "room_not_found", message = result.Error }, statusCode: 404);

            // Anggota yang tersisa melihat keanggotaan berubah secara realtime —
            // tanpa ini, daftar member & jumlah anggota di klien lain basi.
            var leaver = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            await notifier.NotifyMemberChangeAsync(roomId, new MemberChangeDto(
                roomId, "left", leaver?.Name ?? "Unknown", leaver?.Name ?? "Unknown", userId.Value), ct);

            return Results.Ok(result);
        });

        // ──────────────────────────────────────────────────────────────────────
        // Messages
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/chat/rooms/{roomId}/messages?page=1&pageSize=50
        // GET /api/chat/rooms/{roomId}/messages?before=<ISO-8601 UTC>&pageSize=50 — keyset
        // "load older": immune to new messages shifting offsets. `page` is ignored with it.
        g.MapGet("/rooms/{roomId:guid}/messages", async (Guid roomId, int? page, int? pageSize, string? before,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 100);

            if (!string.IsNullOrWhiteSpace(before))
            {
                // Parsed by hand so the cursor is always a UTC DateTime (timestamptz rejects
                // Unspecified kinds); an offset-less value is taken as UTC.
                if (!DateTimeOffset.TryParse(before, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var cursor))
                    return Results.Json(new { code = "invalid_cursor", message = "before must be an ISO-8601 timestamp." }, statusCode: 400);
                return Results.Ok(await svc.GetMessagesBeforeAsync(roomId, userId.Value, cursor.UtcDateTime, size, ct));
            }

            var result = await svc.GetMessagesAsync(roomId, userId.Value, p, size, ct);
            return Results.Ok(result);
        });

        // POST /api/chat/rooms/{roomId}/messages — send message
        g.MapPost("/rooms/{roomId:guid}/messages", async (Guid roomId, SendMessageBody body,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var req = new SendMessageRequest(roomId, body.Content, body.IdempotencyKey, body.ReplyToId, body.Type, body.AttachmentIds);
            var result = await svc.SendMessageAsync(userId.Value, req, ct);

            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    var e when e?.Contains("conflict") == true => 409,
                    var e when e?.Contains("content_too_long") == true => 422,
                    var e when e?.Contains("message_not_found") == true => 404,
                    var e when e?.Contains("not a member") == true => 403,
                    _ => 422
                };
                return Results.Json(new { code = ErrorCode(result.Error), message = result.Error }, statusCode: statusCode);
            }

            if (result.Message is not null)
                await notifier.NotifyNewMessageAsync(roomId, result.Message, ct);

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // PUT /api/chat/rooms/{roomId}/messages/{id} — edit
        g.MapPut("/rooms/{roomId:guid}/messages/{id:guid}", async (Guid roomId, Guid id, EditRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.EditMessageAsync(userId.Value, id, req.Content, ct);

            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    var e when e?.Contains("forbidden") == true || e?.Contains("not the sender") == true => 403,
                    var e when e?.Contains("expired") == true => 422,
                    var e when e?.Contains("content_too_long") == true => 422,
                    var e when e?.Contains("not found") == true => 404,
                    _ => 422
                };
                var code = result.Error switch
                {
                    var e when e?.Contains("expired") == true => "edit_window_expired",
                    var e when e?.Contains("content_too_long") == true => "content_too_long",
                    var e when e?.Contains("not found") == true => "message_not_found",
                    _ => "forbidden"
                };
                return Results.Json(new { code, message = result.Error }, statusCode: statusCode);
            }

            if (result.Message is not null)
                await notifier.NotifyMessageEditedAsync(result.Message.RoomId, result.Message, ct);

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // DELETE /api/chat/rooms/{roomId}/messages/{id} — delete
        g.MapDelete("/rooms/{roomId:guid}/messages/{id:guid}", async (Guid roomId, Guid id,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db,
            IPermissionEvaluator permEval, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var isAdmin = await permEval.IsAllowedAsync(userId.Value, "chat.admin", null);
            var result = await svc.DeleteMessageAsync(userId.Value, id, isAdmin, ct);

            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    var e when e?.Contains("forbidden") == true || e?.Contains("permission") == true => 403,
                    var e when e?.Contains("not found") == true => 404,
                    _ => 422
                };
                var code = result.Error?.Contains("not found") == true ? "message_not_found" : "forbidden";
                return Results.Json(new { code, message = result.Error }, statusCode: statusCode);
            }

            var messageRoomId = result.Message?.RoomId ?? roomId;
            var replacement = result.Message?.DeletedByType == "admin"
                ? "This message was deleted by an admin"
                : "This message was deleted";
            await notifier.NotifyMessageDeletedAsync(messageRoomId, id, replacement, ct);

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // POST /api/chat/rooms/{roomId}/messages/{id}/forward — forward
        g.MapPost("/rooms/{roomId:guid}/messages/{id:guid}/forward", async (Guid roomId, Guid id, ForwardRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.ForwardMessageAsync(userId.Value, id, req.TargetRoomId, ct);

            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    var e when e?.Contains("permission") == true || e?.Contains("not a member") == true => 403,
                    var e when e?.Contains("not found") == true => 404,
                    _ => 422
                };
                return Results.Json(new { code = ErrorCode(result.Error), message = result.Error }, statusCode: statusCode);
            }

            if (result.Message is not null)
                await notifier.NotifyNewMessageAsync(req.TargetRoomId, result.Message, ct);

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // ──────────────────────────────────────────────────────────────────────
        // Threads
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/chat/messages/{parentId}/thread?page=1&pageSize=50
        g.MapGet("/messages/{parentId:guid}/thread", async (Guid parentId, int? page, int? pageSize,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 100);

            var result = await svc.GetThreadRepliesAsync(parentId, userId.Value, p, size, ct);
            return Results.Ok(result);
        });

        // POST /api/chat/messages/{parentId}/thread — reply in thread
        g.MapPost("/messages/{parentId:guid}/thread", async (Guid parentId, SendMessageBody body,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var req = new SendMessageRequest(body.RoomId ?? Guid.Empty, body.Content, body.IdempotencyKey, body.ReplyToId, body.Type, body.AttachmentIds);
            var result = await svc.SendThreadReplyAsync(userId.Value, parentId, req, ct);

            if (!result.Success)
            {
                var code = result.Error?.Contains("full") == true ? "thread_full" : ErrorCode(result.Error);
                var statusCode = result.Error?.Contains("full") == true ? 422 : 404;
                return Results.Json(new { code, message = result.Error }, statusCode: statusCode);
            }

            if (result.Message is not null)
                await notifier.NotifyNewMessageAsync(result.Message.RoomId, result.Message, ct);

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // ──────────────────────────────────────────────────────────────────────
        // Reactions
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/chat/messages/{id}/reactions — toggle
        g.MapPost("/messages/{id:guid}/reactions", async (Guid id, ReactRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.ToggleReactionAsync(userId.Value, id, req.Emoji, ct);

            if (!result.Success)
            {
                var code = result.Error?.Contains("limit") == true ? "reaction_limit" : ErrorCode(result.Error);
                return Results.Json(new { code, message = result.Error }, statusCode: 422);
            }

            if (result.Reaction is not null)
            {
                // Get the roomId from the message for notifying the room
                var msg = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
                if (msg is not null)
                    await notifier.NotifyReactionAsync(msg.RoomId, id, result.Reaction, ct);
            }

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // POST /api/chat/messages/{id}/poll/vote — toggle my vote; broadcast as an edit so tallies update live
        g.MapPost("/messages/{id:guid}/poll/vote", async (Guid id, PollVoteRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.VotePollAsync(userId.Value, id, req.OptionIndex, ct);
            if (!result.Success)
            {
                var (statusCode, code) = result.Error switch
                {
                    "message_not_found" => (404, "message_not_found"),
                    "not a member" => (403, "forbidden"),
                    "conflict" => (409, "conflict"),
                    var e => (422, e ?? "invalid"),
                };
                return Results.Json(new { code, message = result.Error }, statusCode: statusCode);
            }

            await notifier.NotifyMessageEditedAsync(result.Message!.RoomId, result.Message, ct);
            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // GET /api/chat/messages/{id}/seen — seen-by list (requires room membership)
        g.MapGet("/messages/{id:guid}/seen", async (Guid id, HttpContext http, IPopDbContext db, IChatService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var detail = await svc.GetSeenDetailAsync(userId.Value, id, ct);
            // legacy fallback: if message not found svc returns empty; distinguish via direct lookup
            var msg2 = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (msg2 is null || msg2.IsDeleted) return Results.Json(new { code = "message_not_found", message = "Message not found." }, statusCode: 404);
            var isMember2 = await db.ChatRoomMembers.AsNoTracking().AnyAsync(m => m.RoomId == msg2.RoomId && m.UserId == userId.Value && m.LeftAt == null, ct);
            if (!isMember2) return Results.Json(new { code = "forbidden", message = "Not a member of this room." }, statusCode: 403);
            return Results.Ok(detail);
        });

        // GET /api/chat/rooms/{roomId}/read-counts?ids=a,b — per-message "read by N of M" for the
        // caller's own group messages, so every bubble can show its count after a seenUpdate.
        g.MapGet("/rooms/{roomId:guid}/read-counts", async (Guid roomId, string? ids, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var isMember = await db.ChatRoomMembers.AsNoTracking().AnyAsync(m => m.RoomId == roomId && m.UserId == userId.Value && m.LeftAt == null, ct);
            if (!isMember) return Results.Json(new { code = "forbidden", message = "Not a member of this room." }, statusCode: 403);
            var idList = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var g2) ? g2 : Guid.Empty).Where(g2 => g2 != Guid.Empty).Distinct().Take(200).ToList();
            if (idList.Count == 0) return Results.Ok(Array.Empty<MessageReadCountDto>());
            var total = await db.ChatRoomMembers.AsNoTracking().CountAsync(m => m.RoomId == roomId && m.LeftAt == null && m.UserId != userId.Value, ct);
            var activeIds = db.ChatRoomMembers.Where(m => m.RoomId == roomId && m.LeftAt == null && m.UserId != userId.Value).Select(m => m.UserId);
            var own = db.ChatMessages.Where(m => m.RoomId == roomId && m.SenderId == userId.Value && idList.Contains(m.Id)).Select(m => m.Id);
            var reads = await db.ChatReadReceipts.AsNoTracking()
                .Where(r => own.Contains(r.MessageId) && r.ReadAt != null && activeIds.Contains(r.UserId))
                .Select(r => new { r.MessageId, r.UserId, r.User.Name, r.User.PhotoPath, r.DeliveredAt, r.ReadAt })
                .ToListAsync(ct);
            var byMsg = reads.GroupBy(r => r.MessageId).ToDictionary(gr => gr.Key, gr => gr.ToList());
            var ownIds = await own.ToListAsync(ct);
            return Results.Ok(ownIds.Select(id =>
            {
                var list = byMsg.GetValueOrDefault(id) ?? [];
                var preview = list.OrderByDescending(r => r.ReadAt).Take(3)
                    .Select(r => new SeenByDto(r.UserId, r.Name, r.PhotoPath, r.DeliveredAt, r.ReadAt)).ToList();
                return new MessageReadCountDto(id, list.Count, total, preview);
            }).ToList());
        });

        // ──────────────────────────────────────────────────────────────────────
        // Pins
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/chat/rooms/{roomId}/pins (requires membership)
        g.MapGet("/rooms/{roomId:guid}/pins", async (Guid roomId, HttpContext http, IPopDbContext db, IChatService svc, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var isMember = await db.ChatRoomMembers.AsNoTracking().AnyAsync(m => m.RoomId == roomId && m.UserId == userId.Value && m.LeftAt == null, ct);
            if (!isMember) return Results.Json(new { code = "forbidden", message = "Not a member of this room." }, statusCode: 403);
            var pins = await svc.GetPinnedMessagesAsync(roomId, ct);
            return Results.Ok(pins);
        });

        // POST /api/chat/rooms/{roomId}/pins/{messageId}
        g.MapPost("/rooms/{roomId:guid}/pins/{messageId:guid}", async (Guid roomId, Guid messageId,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.PinMessageAsync(userId.Value, messageId, ct);

            if (!result.Success)
            {
                // The service reports "forbidden" (not a member) — that's a 403, not a
                // validation failure; the pin limit and a missing message stay 422.
                var statusCode = result.Error == "forbidden" ? 403 : 422;
                var code = result.Error ?? "forbidden";
                return Results.Json(new { code, message = result.Error }, statusCode: statusCode);
            }

            // Already pinned / lost a concurrent race: nothing changed, so nothing to tell anyone.
            if (!result.Changed) return Results.Ok(result);
            var broadcastRoomId = result.RoomId ?? roomId;
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            await notifier.NotifyPinnedChangeAsync(broadcastRoomId, new PinnedChangeDto(broadcastRoomId, messageId, true, user?.Name ?? "Unknown", result.SystemMessage), ct);
            db.AuditLogs.Add(new AuditLog { ActorNik = http.User.FindFirst("nik")?.Value, Action = "chat.pin", Entity = "ChatMessage", EntityId = messageId.ToString(), IpAddress = http.Connection.RemoteIpAddress?.ToString(), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // DELETE /api/chat/rooms/{roomId}/pins/{messageId}
        g.MapDelete("/rooms/{roomId:guid}/pins/{messageId:guid}", async (Guid roomId, Guid messageId,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.UnpinMessageAsync(userId.Value, messageId, ct);

            if (!result.Success)
            {
                var statusCode = result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error ?? "forbidden", message = result.Error }, statusCode: statusCode);
            }

            // Already unpinned (repeat click, other tab, concurrent unpin): no event, no audit row.
            if (!result.Changed) return Results.Ok(result);
            var broadcastRoomId2 = result.RoomId ?? roomId;
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            // The persisted "X unpinned a message" row rides on pinnedChanged (not newMessage),
            // so every member sees it live without unread/push/sound side effects.
            await notifier.NotifyPinnedChangeAsync(broadcastRoomId2, new PinnedChangeDto(broadcastRoomId2, messageId, false, user?.Name ?? "Unknown", result.SystemMessage), ct);
            db.AuditLogs.Add(new AuditLog { ActorNik = http.User.FindFirst("nik")?.Value, Action = "chat.unpin", Entity = "ChatMessage", EntityId = messageId.ToString(), IpAddress = http.Connection.RemoteIpAddress?.ToString(), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // ──────────────────────────────────────────────────────────────────────
        // Simpanan pribadi (saved messages) — per pengguna, tanpa siaran ruang
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/chat/saved — daftar simpanan milik pengguna yang sedang login
        g.MapGet("/saved", async (HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await svc.GetSavedMessagesAsync(userId.Value, ct));
        });

        // POST /api/chat/saved/{messageId}
        g.MapPost("/saved/{messageId:guid}", async (Guid messageId,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.SaveMessageAsync(userId.Value, messageId, ct);
            if (!result.Success)
            {
                var sc = result.Error == "forbidden" ? 403 : result.Error == "message_not_found" ? 404 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: sc);
            }
            db.AuditLogs.Add(new AuditLog { ActorNik = http.User.FindFirst("nik")?.Value, Action = "chat.save", Entity = "ChatMessage", EntityId = messageId.ToString(), IpAddress = http.Connection.RemoteIpAddress?.ToString(), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        // DELETE /api/chat/saved/{messageId}
        g.MapDelete("/saved/{messageId:guid}", async (Guid messageId,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.UnsaveMessageAsync(userId.Value, messageId, ct);
            if (!result.Success)
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: 404);
            db.AuditLogs.Add(new AuditLog { ActorNik = http.User.FindFirst("nik")?.Value, Action = "chat.unsave", Entity = "ChatMessage", EntityId = messageId.ToString(), IpAddress = http.Connection.RemoteIpAddress?.ToString(), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        // ──────────────────────────────────────────────────────────────────────
        // Search
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/chat/rooms/search?q=
        g.MapGet("/rooms/search", async (string? q,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
                return Results.Json(new { code = "search_query_too_short", message = "Search query must be at least 2 characters." }, statusCode: 422);

            var result = await svc.SearchRoomsAsync(userId.Value, q.Trim(), ct);
            return Results.Ok(result);
        });

        // GET /api/chat/mentions/unread?page=1&pageSize=20 — mention belum dibaca
        // (pesan yang menyebut pengguna setelah tanda baca terakhir tiap ruang).
        g.MapGet("/mentions/unread", async (int? page, int? pageSize,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 50);
            return Results.Ok(await svc.GetUnreadMentionsAsync(userId.Value, p, size, ct));
        });

        // GET /api/chat/search?q=&page=1&pageSize=20 — pencarian pesan lintas ruang
        // (semua ruang yang diikuti pengguna). Dipakai halaman /chat/search.
        g.MapGet("/search", async (string? q, int? page, int? pageSize,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
                return Results.Json(new { code = "search_query_too_short", message = "Search query must be at least 2 characters." }, statusCode: 422);

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 50);

            var result = await svc.SearchAllMessagesAsync(userId.Value, q.Trim(), p, size, ct);
            return Results.Ok(result);
        });

        // GET /api/chat/rooms/{roomId}/search?q=&page=1&pageSize=20
        g.MapGet("/rooms/{roomId:guid}/search", async (Guid roomId, string? q, int? page, int? pageSize,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
                return Results.Json(new { code = "search_query_too_short", message = "Search query must be at least 2 characters." }, statusCode: 422);

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 50);

            var result = await svc.SearchMessagesAsync(userId.Value, roomId, q.Trim(), p, size, ct);
            return Results.Ok(result);
        });

        // ──────────────────────────────────────────────────────────────────────
        // Mute & Archive
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/chat/rooms/{roomId}/mute — { duration }
        g.MapPost("/rooms/{roomId:guid}/mute", async (Guid roomId, MuteRequest req,
            HttpContext http, IChatService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var duration = ParseMuteDuration(req.Duration);
            if (duration is null)
                return Results.Json(new { code = "invalid_duration", message = "Duration must be one of: 1h, 8h, 1w, forever" }, statusCode: 422);

            var muteResult = await svc.MuteRoomAsync(userId.Value, roomId, duration.Value, ct);
            if (!muteResult.Success) return Results.Json(new { code = muteResult.Error ?? "forbidden", message = muteResult.Error }, statusCode: 403);
            await RoomsChangedAsync(http, realtime, ct);
            return Results.Ok(muteResult);
        });

        // DELETE /api/chat/rooms/{roomId}/mute
        g.MapDelete("/rooms/{roomId:guid}/mute", async (Guid roomId,
            HttpContext http, IChatService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var r2 = await svc.UnmuteRoomAsync(userId.Value, roomId, ct);
            if (!r2.Success) return Results.Json(new { code = r2.Error ?? "forbidden", message = r2.Error }, statusCode: 403);
            await RoomsChangedAsync(http, realtime, ct);
            return Results.Ok(r2);
        });

        // POST /api/chat/rooms/{roomId}/archive
        g.MapPost("/rooms/{roomId:guid}/archive", async (Guid roomId,
            HttpContext http, IChatService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var ar = await svc.ArchiveRoomAsync(userId.Value, roomId, ct);
            if (!ar.Success) return Results.Json(new { code = ar.Error ?? "forbidden", message = ar.Error }, statusCode: 403);
            await RoomsChangedAsync(http, realtime, ct);
            return Results.Ok(ar);
        });

        // DELETE /api/chat/rooms/{roomId}/archive
        g.MapDelete("/rooms/{roomId:guid}/archive", async (Guid roomId,
            HttpContext http, IChatService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var ur = await svc.UnarchiveRoomAsync(userId.Value, roomId, ct);
            if (!ur.Success) return Results.Json(new { code = ur.Error ?? "forbidden", message = ur.Error }, statusCode: 403);
            await RoomsChangedAsync(http, realtime, ct);
            return Results.Ok(ur);
        });

        g.MapPost("/rooms/{roomId:guid}/clear-history", async (Guid roomId,
            HttpContext http, IChatService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.ClearRoomHistoryAsync(userId.Value, roomId, ct);
            if (result.Success) await RoomsChangedAsync(http, realtime, ct);
            return result.Success ? Results.Ok(result)
                : Results.Json(new { code = result.Error }, statusCode: 403);
        });

        // "Hapus chat" for me: history cleared + room hidden until a new message arrives.
        g.MapPost("/rooms/{roomId:guid}/delete-for-me", async (Guid roomId,
            HttpContext http, IChatService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.DeleteRoomForMeAsync(userId.Value, roomId, ct);
            if (!result.Success) return Results.Json(new { code = result.Error }, statusCode: 403);
            await RoomsChangedAsync(http, realtime, ct);
            return Results.Ok(result);
        });

        // Group owner/admin: delete the group, its messages and its files for everyone.
        g.MapPost("/rooms/{roomId:guid}/delete-for-everyone", async (Guid roomId, DeleteRoomRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IFileAccessService fileSvc,
            IWebHostEnvironment env, IPopDbContext db, ILoggerFactory logs, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.DeleteGroupForEveryoneAsync(userId.Value, roomId, req.ConfirmName, ct);
            if (!result.Success)
                return Results.Json(new { code = result.Error }, statusCode: result.Error switch
                {
                    "room_not_found" => 404,
                    "forbidden" => 403,
                    _ => 422,
                });

            foreach (var member in result.MemberIds)
                await notifier.NotifyRemovedAsync(member, roomId, ct);

            var log = logs.CreateLogger("ChatEndpoints");
            foreach (var file in result.OrphanFiles)
            {
                try
                {
                    if (file.Server == "local")
                    {
                        if (TryLocalFile(env.ContentRootPath, file.Server, file.Path, out var local)) File.Delete(local);
                    }
                    else if (fileSvc.HasServiceAccount)
                        await fileSvc.DeleteAsServiceAsync(file.Path, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The rows are gone already; a leftover file is only wasted space.
                    log.LogWarning(ex, "Could not delete chat file {Path} of deleted room {RoomId}", file.Path, roomId);
                }
            }
            return Results.Ok(new { success = true });
        });

        g.MapGet("/blocked-users", async (HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            return userId is null ? Results.Unauthorized() : Results.Ok(await svc.GetBlockedUserIdsAsync(userId.Value, ct));
        });

        g.MapPost("/users/{targetUserId:guid}/block", async (Guid targetUserId,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            return await svc.BlockUserAsync(userId.Value, targetUserId, ct) ? Results.Ok() : Results.BadRequest();
        });

        g.MapDelete("/users/{targetUserId:guid}/block", async (Guid targetUserId,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            await svc.UnblockUserAsync(userId.Value, targetUserId, ct);
            return Results.Ok();
        });

        // PUT /api/chat/rooms/{roomId}/read-receipts — { "enabled": bool }
        // Per-user setting for this room. For DMs this is reciprocal: the other party only sees "read" receipts if you enable it.
        g.MapPut("/rooms/{roomId:guid}/read-receipts", async (Guid roomId, ReadReceiptsRequest req,
            HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var r = await svc.SetReadReceiptsAsync(userId.Value, roomId, req.Enabled, ct);
            if (!r.Success) return Results.Json(new { code = r.Error ?? "forbidden", message = r.Error }, statusCode: 403);
            return Results.Ok(r);
        });

        // Group-page-enhancement endpoints
        g.MapPut("/rooms/{roomId:guid}", async (Guid roomId, UpdateGroupIdentityRequest req, HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await svc.UpdateGroupIdentityAsync(userId.Value, roomId, req.Name, req.Description, req.AvatarColor, ct);
            if (!r.Success)
            {
                var code = r.Error is "invalid_group_name" or "invalid_group_description" or "not_a_group" or "invalid_avatar_color" ? r.Error : "forbidden";
                var sc = code == "forbidden" ? 403 : 422;
                return Results.Json(new { code, message = r.Error }, statusCode: sc);
            }
            var roomEntity = await db.ChatRooms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == roomId, ct);
            if (roomEntity != null) await notifier.NotifyRoomUpdatedAsync(roomId, new RoomUpdateDto(roomId, roomEntity.Name, roomEntity.LastMessageAt), ct);
            return Results.Ok(r);
        });
        // Group photo: images only, ≤2 MB, owner/admin. Stored as a 256px JPEG under a random
        // name and served anonymously (an <img> cannot send the bearer header; the name is unguessable).
        g.MapPost("/rooms/{roomId:guid}/avatar", async (Guid roomId, HttpContext http, IChatService svc, IChatNotifier notifier,
            IPopDbContext db, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!http.Request.HasFormContentType) return Results.Json(new { code = "no_file", message = "Choose an image." }, statusCode: 422);
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            var check = ValidateGroupAvatar(file?.FileName, file?.Length ?? 0);
            if (check is not null) return Results.Json(new { code = check, message = check }, statusCode: 422);
            byte[] jpeg;
            try
            {
                await using var src = file!.OpenReadStream();
                jpeg = CreateSquareAvatar(src, 256);
            }
            catch { return Results.Json(new { code = "invalid_image", message = "The file is not a readable image." }, statusCode: 422); }

            var name = $"{Guid.NewGuid():N}.jpg";
            var url = $"/api/chat-avatars/{name}";
            var r = await svc.SetGroupAvatarAsync(userId.Value, roomId, url, ct);
            if (!r.Success) return Results.Json(new { code = r.Error ?? "forbidden", message = r.Error }, statusCode: r.Error == "forbidden" ? 403 : 422);
            var dir = Path.Combine(env.ContentRootPath, "chat-files", "avatars");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, name), jpeg, ct);
            DeleteGroupAvatarFile(env.ContentRootPath, r.Error);
            var room = await db.ChatRooms.AsNoTracking().FirstAsync(x => x.Id == roomId, ct);
            await notifier.NotifyRoomUpdatedAsync(roomId, new RoomUpdateDto(roomId, room.Name, room.LastMessageAt), ct);
            return Results.Ok(new { avatarUrl = url });
        }).DisableAntiforgery();
        g.MapDelete("/rooms/{roomId:guid}/avatar", async (Guid roomId, HttpContext http, IChatService svc, IChatNotifier notifier,
            IPopDbContext db, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await svc.SetGroupAvatarAsync(userId.Value, roomId, null, ct);
            if (!r.Success) return Results.Json(new { code = r.Error ?? "forbidden", message = r.Error }, statusCode: r.Error == "forbidden" ? 403 : 422);
            DeleteGroupAvatarFile(env.ContentRootPath, r.Error);
            var room = await db.ChatRooms.AsNoTracking().FirstAsync(x => x.Id == roomId, ct);
            await notifier.NotifyRoomUpdatedAsync(roomId, new RoomUpdateDto(roomId, room.Name, room.LastMessageAt), ct);
            return Results.NoContent();
        });
        g.MapGet("/rooms/{roomId:guid}/audit", async (Guid roomId, int? page, int? pageSize, HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            // ipop-50: page was the ONE paged param in this file passed through raw. Five sibling
            // endpoints (:35/:109/:249/:416/:532) all clamp with Math.Max(1, ...), and no service
            // clamps page internally, so page=0 reached Skip((0-1)*20) = Skip(-20) and Postgres
            // rejected it: 500 "OFFSET must not be negative". Reproduced live before fixing.
            // pageSize needs no clamp here — GetMembershipAuditAsyncImpl:1723 already guards it.
            var result = await svc.GetMembershipAuditAsync(userId.Value, roomId, Math.Max(1, page ?? 1), pageSize ?? 20, ct);
            if (result.TotalCount == 0)
            {
                // distinguish forbidden (empty because forbidden) — check role
                var m = await db.ChatRoomMembers.AsNoTracking().FirstOrDefaultAsync(x => x.RoomId == roomId && x.UserId == userId.Value && x.LeftAt == null, ct);
                if (m?.Role is not ("owner" or "admin")) return Results.Json(new { code = "forbidden", message = "Forbidden" }, statusCode: 403);
            }
            return Results.Ok(result);
        });
        g.MapPost("/rooms/{roomId:guid}/pin", async (Guid roomId, HttpContext http, IChatService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await svc.PinRoomAsync(userId.Value, roomId, ct);
            if (!r.Success) return Results.Json(new { code = r.Error ?? "forbidden", message = r.Error }, statusCode: r.Error == "pin_limit_reached" ? 422 : 403);
            await RoomsChangedAsync(http, realtime, ct);
            return Results.Ok(r);
        });
        g.MapDelete("/rooms/{roomId:guid}/pin", async (Guid roomId, HttpContext http, IChatService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await svc.UnpinRoomAsync(userId.Value, roomId, ct);
            if (!r.Success) return Results.Json(new { code = r.Error ?? "forbidden", message = r.Error }, statusCode: 403);
            await RoomsChangedAsync(http, realtime, ct);
            return Results.Ok(r);
        });
        g.MapGet("/rooms/archived", async (int? page, int? pageSize, HttpContext http, IChatService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            // ipop-47: clamp for symmetry with the sibling endpoints (:36/:110/:250/:417) —
            // GetArchivedRoomsAsync does not clamp internally, so a hand-crafted
            // pageSize used to be unbounded. Default 50 (not ?? 20) matches the client's
            // request in ChatState and the ?? 50 convention of the messages/thread endpoints.
            var result = await svc.GetArchivedRoomsAsync(userId.Value, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, 100), ct);
            return Results.Ok(result);
        });

        // ──────────────────────────────────────────────────────────────────────
        // Group Management
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/chat/rooms/{roomId}/members — { userId }
        g.MapPost("/rooms/{roomId:guid}/members", async (Guid roomId, AddMemberRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.AddMemberAsync(userId.Value, roomId, req.UserId, ct);

            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    var e when e?.Contains("permission") == true || e?.Contains("role") == true => 403,
                    var e when e?.Contains("full") == true => 422,
                    _ => 422
                };
                var code = result.Error?.Contains("full") == true ? "group_full" : "forbidden";
                return Results.Json(new { code, message = result.Error }, statusCode: statusCode);
            }

            var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.UserId, ct);
            await notifier.NotifyMemberChangeAsync(roomId, new MemberChangeDto(
                roomId, "added", actor?.Name ?? "Unknown", target?.Name ?? "Unknown", req.UserId), ct);
            await notifier.NotifyAddedAsync([req.UserId], roomId, ct);

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // DELETE /api/chat/rooms/{roomId}/members/{userId}
        g.MapDelete("/rooms/{roomId:guid}/members/{memberId:guid}", async (Guid roomId, Guid memberId,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.RemoveMemberAsync(userId.Value, roomId, memberId, ct);

            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    var e when e?.Contains("permission") == true || e?.Contains("role") == true || e?.Contains("cannot") == true => 403,
                    _ => 422
                };
                return Results.Json(new { code = "forbidden", message = result.Error }, statusCode: statusCode);
            }

            var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == memberId, ct);
            await notifier.NotifyMemberChangeAsync(roomId, new MemberChangeDto(
                roomId, "removed", actor?.Name ?? "Unknown", target?.Name ?? "Unknown", memberId), ct);
            await notifier.NotifyRemovedAsync(memberId, roomId, ct);

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // PUT /api/chat/rooms/{roomId}/members/{userId}/role — { role }
        g.MapPut("/rooms/{roomId:guid}/members/{memberId:guid}/role", async (Guid roomId, Guid memberId, ChangeRoleRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var newRole = req.Role?.ToLowerInvariant() switch
            {
                "admin" => ChatMemberRole.Admin,
                "member" => ChatMemberRole.Member,
                _ => (ChatMemberRole?)null
            };
            if (newRole is null)
                return Results.Json(new { code = "invalid_role", message = "Role must be 'admin' or 'member'." }, statusCode: 422);

            var result = await svc.ChangeRoleAsync(userId.Value, roomId, memberId, newRole.Value, ct);

            if (!result.Success)
            {
                var statusCode = result.Error?.Contains("permission") == true || result.Error?.Contains("cannot") == true ? 403 : 422;
                return Results.Json(new { code = "forbidden", message = result.Error }, statusCode: statusCode);
            }

            var actor = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            var target = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == memberId, ct);
            var action = newRole == ChatMemberRole.Admin ? "promoted" : "demoted";
            await notifier.NotifyMemberChangeAsync(roomId, new MemberChangeDto(
                roomId, action, actor?.Name ?? "Unknown", target?.Name ?? "Unknown", memberId), ct);

            return Results.Ok(result);
        }).RequirePermission("chat.send");

        // ──────────────────────────────────────────────────────────────────────
        // Emergency Broadcast
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/chat/rooms/{roomId}/emergency — { content }
        g.MapPost("/rooms/{roomId:guid}/emergency", async (Guid roomId, EmergencyRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.SendEmergencyBroadcastAsync(userId.Value, roomId, req.Content, ct);

            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    var e when e?.Contains("permission") == true || e?.Contains("role") == true => 403,
                    var e when e?.Contains("rate") == true => 429,
                    _ => 422
                };
                var code = result.Error?.Contains("rate") == true ? "emergency_rate_limited" : "forbidden";
                return Results.Json(new { code, message = result.Error }, statusCode: statusCode);
            }

            if (result.Message is not null)
                await notifier.NotifyNewMessageAsync(roomId, result.Message, ct);

            return Results.Ok(result);
        }).RequirePermission("chat.admin");

        // ──────────────────────────────────────────────────────────────────────
        // File Attachments
        // ──────────────────────────────────────────────────────────────────────

        // POST /api/chat/attachments/upload — multipart: roomId, file(s)
        g.MapPost("/attachments/upload", async (HttpContext http,
            IPopDbContext db, IFileAccessService fileSvc, IWebHostEnvironment env, IConfiguration cfg, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var form = await http.Request.ReadFormAsync(ct);
            var roomIdStr = form["roomId"].ToString();
            if (!Guid.TryParse(roomIdStr, out var roomId))
                return Results.Json(new { code = "room_id_required", message = "roomId is required." }, statusCode: 422);

            // Verify user is a member of the room
            var isMember = await db.ChatRoomMembers.AsNoTracking()
                .AnyAsync(m => m.RoomId == roomId && m.UserId == userId.Value && m.LeftAt == null, ct);
            if (!isMember)
                return Results.Json(new { code = "room_not_found", message = "Room does not exist or you are not a member." }, statusCode: 404);

            var files = form.Files;
            if (files.Count == 0)
                return Results.Json(new { code = "file_required", message = "At least one file is required." }, statusCode: 422);
            if (files.Count > 5)
                return Results.Json(new { code = "too_many_attachments", message = "Maximum 5 files per upload." }, statusCode: 422);

            const long maxFileSize = 50L * 1024 * 1024; // 50MB
            foreach (var file in files)
            {
                if (file.Length > maxFileSize)
                    return Results.Json(new { code = "file_too_large", message = $"File '{file.FileName}' exceeds the 50MB limit." }, statusCode: 422);
            }

            var chatRoot = cfg["Synology:ChatFolder"]?.Trim().TrimEnd('/') ?? "/home/chat";
            if (!chatRoot.StartsWith('/')) chatRoot = "/" + chatRoot;
            var chatPath = $"{chatRoot}/{roomId}";

            // Storage target is the shared service account on the NAS, with local disk as the
            // fallback when none is configured (dev) or the NAS is unreachable. Per-user Synology
            // credentials are deliberately NOT consulted here: see the upload branch for why the
            // writer identity has to be the same one every reader uses.

            var attachments = new List<AttachmentDto>();

            foreach (var file in files)
            {
                var safeName = SanitizeFileName(file.FileName);
                var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
                // Never trust file.ContentType alone — Blazor's StreamContent sends no
                // Content-Type at all, which used to persist MimeType = "" and made every
                // attachment (images included) render as an untyped file card.
                var mime = ResolveMime(safeName, file.ContentType);
                var isImage = IsImageMime(mime);
                var isRaster = IsRasterImageMime(mime);
                string? thumbName = isRaster ? $"{Path.GetFileNameWithoutExtension(uniqueName)}.thumb.jpg" : null;
                byte[]? thumbBytes = null;

                // Buffer once so we can reuse for thumb generation + storage
                await using var buffered = new MemoryStream();
                await using (var src = file.OpenReadStream())
                    await src.CopyToAsync(buffered, ct);

                if (isRaster && thumbName is not null)
                {
                    try
                    {
                        buffered.Position = 0;
                        thumbBytes = CreateThumbnail(buffered, 640);
                    }
                    catch { thumbBytes = null; /* ponytail: fallback to full file as thumbnail */ }
                    buffered.Position = 0;
                }
                else buffered.Position = 0;

                string server = "local";
                string storedPath = "";
                string? thumbnailPath = null;
                var uploadedToSynology = false;

                // Chat attachments are written by the SERVICE account, never by the sender: the
                // recipient has to read the same bytes back, and a file under the sender's own
                // Synology home is a different physical path (and permission set) for everyone
                // else. Writer and readers must be one identity.
                if (fileSvc.HasServiceAccount)
                {
                    try
                    {
                        buffered.Position = 0;
                        await fileSvc.UploadAsServiceAsync(chatPath, uniqueName, buffered, ct);
                        server = fileSvc.ServiceServer!;
                        storedPath = $"{chatPath}/{uniqueName}";
                        uploadedToSynology = true;

                        if (thumbBytes is not null && thumbName is not null)
                        {
                            try
                            {
                                await using var thumbStream = new MemoryStream(thumbBytes);
                                await fileSvc.UploadAsServiceAsync(chatPath, thumbName, thumbStream, ct);
                                thumbnailPath = $"{chatPath}/{thumbName}";
                            }
                            catch
                            {
                                thumbnailPath = storedPath;
                            }
                        }
                        else if (isImage)
                        {
                            thumbnailPath = storedPath;
                        }
                    }
                    catch (Exception ex)
                    {
                        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ChatEndpoints");
                        logger?.LogWarning(ex, "Synology service-account upload failed for room {RoomId} to {Server}:{Path}, falling back to local storage: {Error}",
                            roomId, fileSvc.ServiceServer, $"{chatPath}/{uniqueName}", ex.Message);
                        uploadedToSynology = false;
                    }
                }

                if (!uploadedToSynology)
                {
                    if (!fileSvc.HasServiceAccount)
                    {
                        // Worth a warning, not a failure: local disk keeps chat working in dev and
                        // is readable by every member (the API serves the bytes), but the files live
                        // on the app server instead of the NAS and are lost if it is rebuilt.
                        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ChatEndpoints");
                        logger?.LogWarning("No Synology service account configured (Synology:ServiceAccount:Username/Password) — chat attachment '{File}' in room {RoomId} stored on local disk instead of the NAS.",
                            safeName, roomId);
                    }

                    buffered.Position = 0;
                    var dir = Path.Combine(env.ContentRootPath, "chat-files", roomId.ToString("N"));
                    Directory.CreateDirectory(dir);
                    var full = Path.Combine(dir, uniqueName);
                    await using (var fs = File.Create(full))
                        await buffered.CopyToAsync(fs, ct);

                    server = "local";
                    storedPath = Path.Combine("chat-files", roomId.ToString("N"), uniqueName).Replace('\\', '/');

                    if (thumbBytes is not null && thumbName is not null)
                    {
                        var thumbFull = Path.Combine(dir, thumbName);
                        await File.WriteAllBytesAsync(thumbFull, thumbBytes, ct);
                        thumbnailPath = Path.Combine("chat-files", roomId.ToString("N"), thumbName).Replace('\\', '/');
                    }
                    else if (isImage)
                    {
                        thumbnailPath = storedPath;
                    }
                }

                var attachment = new ChatAttachment
                {
                    Id = Guid.NewGuid(),
                    MessageId = null, // Will be linked when message is sent
                    FileName = safeName,
                    FileSize = file.Length,
                    MimeType = mime,
                    SynologyServer = server,
                    SynologyPath = storedPath,
                    ThumbnailPath = thumbnailPath,
                    CreatedAt = DateTime.UtcNow,
                };

                db.ChatAttachments.Add(attachment);
                attachments.Add(new AttachmentDto(
                    attachment.Id, attachment.FileName, attachment.FileSize,
                    attachment.MimeType, attachment.ThumbnailPath));
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(attachments);
        }).RequirePermission("chat.send").DisableAntiforgery();

        // GET /api/chat/attachments/{id}/download?inline=true
        // inline=true drops the Content-Disposition filename so the browser renders the
        // bytes in place (PDF in an iframe, <video>, <img>) instead of forcing a save.
        g.MapGet("/attachments/{id:guid}/download", async (Guid id, bool? inline,
            HttpContext http, IPopDbContext db, IFileAccessService fileSvc, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var attachment = await db.ChatAttachments.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (attachment is null || !await CanAccessAttachmentAsync(db, attachment, userId.Value, ct))
                return Results.Json(new { code = "not_found", message = "Attachment not found." }, statusCode: 404);

            var mime = await EnsureMimeAsync(db, attachment, ct);
            var asInline = inline == true;
            var name = asInline ? null : attachment.FileName;

            if (TryLocalFile(env.ContentRootPath, attachment.SynologyServer, attachment.SynologyPath, out var localFile))
                return CachedFile(http, Results.File(localFile, mime, name, enableRangeProcessing: true));

            try
            {
                var stream = await DownloadAttachmentAsync(fileSvc, attachment.SynologyPath, ct);
                return CachedFile(http, Results.File(stream, mime, name));
            }
            catch (SynoException ex) when (ex.IsAuthFailure)
            {
                return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401);
            }
            catch (SynoException ex)
            {
                return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502);
            }
        });

        // GET /api/chat/attachments/{id}/thumbnail
        // Self-healing: rows uploaded before MIME resolution was fixed have MimeType = ""
        // and ThumbnailPath = null. When the file is a decodable raster image we generate
        // the thumbnail on first view and persist it, so old messages repair themselves.
        g.MapGet("/attachments/{id:guid}/thumbnail", async (Guid id,
            HttpContext http, IPopDbContext db, IFileAccessService fileSvc, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var attachment = await db.ChatAttachments.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (attachment is null || !await CanAccessAttachmentAsync(db, attachment, userId.Value, ct))
                return Results.Json(new { code = "not_found", message = "Attachment not found." }, statusCode: 404);

            var mime = await EnsureMimeAsync(db, attachment, ct);

            if (string.IsNullOrEmpty(attachment.ThumbnailPath))
            {
                if (!IsImageMime(mime))
                    return Results.Json(new { code = "no_thumbnail", message = "No thumbnail available for this attachment." }, statusCode: 404);

                // Vector/undecodable images: the original file *is* the preview.
                if (!IsRasterImageMime(mime))
                {
                    attachment.ThumbnailPath = attachment.SynologyPath;
                    await db.SaveChangesAsync(ct);
                }
                else
                {
                    var generated = await TryBackfillThumbnailAsync(db, fileSvc, env, attachment, ct);
                    if (!generated)
                        return Results.Json(new { code = "no_thumbnail", message = "No thumbnail available for this attachment." }, statusCode: 404);
                }
            }

            var thumbMime = attachment.ThumbnailPath!.EndsWith(".thumb.jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : mime;
            if (TryLocalFile(env.ContentRootPath, attachment.SynologyServer, attachment.ThumbnailPath, out var localThumb))
                return CachedFile(http, Results.File(localThumb, thumbMime, fileDownloadName: null, enableRangeProcessing: true));

            try
            {
                var stream = await DownloadAttachmentAsync(fileSvc, attachment.ThumbnailPath, ct);
                return CachedFile(http, Results.File(stream, thumbMime));
            }
            catch (SynoException ex) when (ex.IsAuthFailure)
            {
                return Results.Json(new { code = "synology_credential_required", message = ex.Message }, statusCode: 401);
            }
            catch (SynoException ex)
            {
                return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502);
            }
        });

        // ──────────────────────────────────────────────────────────────────────
        // User Search (for new DM/group creation)
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/chat/users/search?q=
        g.MapGet("/users/search", async (string? q, IPopDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
                return Results.Json(new { code = "search_query_too_short", message = "Search query must be at least 2 characters." }, statusCode: 422);

            var term = q.Trim();
            var pat = $"%{EscapeLike(term)}%";

            var users = await db.Users.AsNoTracking()
                .Where(u => u.IsActive &&
                    (EF.Functions.ILike(u.Name, pat) || EF.Functions.ILike(u.Email, pat)))
                .OrderBy(u => u.Name)
                .Take(20)
                .Select(u => new { u.Id, u.Nik, u.Name, u.Email })
                .ToListAsync(ct);

            return Results.Ok(users);
        });

        // GET /api/chat/users/directory?q= — the new-conversation picker shows the whole
        // staff list (not just recent chats), so it needs position + department up front.
        // Excludes the caller: you cannot open a conversation with yourself.
        g.MapGet("/users/directory", async (string? q, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var query = db.Users.AsNoTracking()
                .Include(u => u.Department)
                .Where(u => u.IsActive && u.Id != userId.Value);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var pat = $"%{EscapeLike(q.Trim())}%";
                query = query.Where(u => EF.Functions.ILike(u.Name, pat) || EF.Functions.ILike(u.Email, pat));
            }

            var now = DateTime.UtcNow;
            var rows = await query
                .OrderBy(u => u.Name)
                .Take(300)
                .Select(u => new
                {
                    u.Id,
                    u.Nik,
                    u.Name,
                    u.Email,
                    u.PresenceLastSeenAt,
                    u.PresenceManualStatus,
                    u.PresenceIsIdle,
                    Department = u.Department != null ? u.Department.Name : null
                })
                .ToListAsync(ct);

            // ComputeStatus is not translatable, so presence is resolved after the query.
            return Results.Ok(rows.Select(u => new
            {
                u.Id,
                u.Nik,
                u.Name,
                u.Email,
                u.Department,
                PresenceStatus = IPop.Core.Presence.PresenceStatus.ComputeStatus(
                    u.PresenceLastSeenAt, u.PresenceManualStatus, now, u.PresenceIsIdle)
            }));
        });

        // GET /api/chat/users/{nik} — single employee profile for the chat avatar drawer.
        // Returns a DirectoryEmployee-shaped row (camelCase) so the client reuses ProfileDrawer
        // verbatim. Manager resolved Nik → Name via a second lookup (1 row, no N+1).
        // Accepts the user id (deep links carry it instead of the NIK) or, for older callers, a NIK.
        g.MapGet("/users/{nik}", async (string nik, IPopDbContext db, CancellationToken ct) =>
        {
            var users = db.Users.Include(x => x.Department).Include(x => x.Site).AsNoTracking();
            var u = Guid.TryParse(nik, out var uid)
                ? await users.FirstOrDefaultAsync(x => x.Id == uid, ct)
                : await users.FirstOrDefaultAsync(x => x.Nik == nik, ct);
            if (u is null || !u.IsActive)
                return Results.Json(new { code = "user_not_found", message = "User not found." }, statusCode: 404);

            string? manager = null;
            if (!string.IsNullOrEmpty(u.ManagerNik))
                manager = await db.Users.AsNoTracking().Where(x => x.Nik == u.ManagerNik).Select(x => x.Name).FirstOrDefaultAsync(ct);

            var status = IPop.Core.Presence.PresenceStatus.ComputeStatus(u.PresenceLastSeenAt, u.PresenceManualStatus, DateTime.UtcNow, u.PresenceIsIdle);
            return Results.Ok(new IPop.Application.Hris.DirectoryEmployee(
                u.Nik, u.Name, null, u.Department?.Name, u.Department?.Code,
                u.Site?.Name, u.Site?.Code, manager, u.Email, u.Extension, u.Location, status, null, u.PhotoPath, UserId: u.Id));
        });

        return app;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// Per-user room state (mute/archive/pin/cleared) changed: the caller's other tabs
    /// reload their room list. Payload-less; the client re-fetches over REST.
    private static Task RoomsChangedAsync(HttpContext http, IRealtime realtime, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        return string.IsNullOrEmpty(nik) ? Task.CompletedTask
            : realtime.ToUserAsync(nik, RealtimeEvents.ChatRoomsChanged, ct: ct);
    }

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik);
        return user?.Id;
    }

    // Attachments are visible once linked to a room the caller belongs to.
    // Pending uploads (MessageId==null) infer the room from the stored path so the
    // composer strip can load its own thumbnail before the message is sent.
    private static async Task<bool> CanAccessAttachmentAsync(IPopDbContext db, ChatAttachment attachment, Guid userId, CancellationToken ct)
    {
        if (attachment.MessageId is not null)
        {
            return await db.ChatMessages.AsNoTracking()
                .Where(m => m.Id == attachment.MessageId)
                .Join(db.ChatRoomMembers.AsNoTracking(), m => m.RoomId, rm => rm.RoomId, (m, rm) => rm)
                .AnyAsync(rm => rm.UserId == userId && rm.LeftAt == null, ct);
        }
        // Pending: room is encoded in the path "/chat/{roomId}/..." or "chat-files/{roomId}/..."
        if (TryParseRoomIdFromChatPath(attachment.SynologyPath, out var pendingRoomId) ||
            (attachment.ThumbnailPath is not null && TryParseRoomIdFromChatPath(attachment.ThumbnailPath, out pendingRoomId)))
        {
            return await db.ChatRoomMembers.AsNoTracking()
                .AnyAsync(rm => rm.RoomId == pendingRoomId && rm.UserId == userId && rm.LeftAt == null, ct);
        }
        return false;
    }

    private static bool TryParseRoomIdFromChatPath(string? path, out Guid roomId)
    {
        roomId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Split on '/' and find first segment that parses as Guid (the roomId)
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(seg, out roomId)) return true;
        }
        return false;
    }

    private static MuteDuration? ParseMuteDuration(string? duration) => duration?.ToLowerInvariant() switch
    {
        "1h" => MuteDuration.OneHour,
        "8h" => MuteDuration.EightHours,
        "1w" => MuteDuration.OneWeek,
        "forever" => MuteDuration.Forever,
        _ => null
    };

    private static string ErrorCode(string? error)
    {
        if (string.IsNullOrEmpty(error)) return "unknown_error";
        return error.ToLowerInvariant() switch
        {
            var e when e.Contains("conflict") => "idempotency_conflict",
            var e when e.Contains("content_too_long") => "content_too_long",
            var e when e.Contains("not found") => "message_not_found",
            var e when e.Contains("permission") || e.Contains("forbidden") => "forbidden",
            var e when e.Contains("rate") => "rate_limited",
            _ => "unknown_error"
        };
    }

    /// Repairs a blank/generic stored MimeType from the filename and persists the fix.
    /// Returns the MIME to use for the response either way.
    private static async Task<string> EnsureMimeAsync(IPopDbContext db, ChatAttachment attachment, CancellationToken ct)
    {
        var current = attachment.MimeType;
        var resolved = ResolveMime(attachment.FileName, current);
        if (!string.Equals(current, resolved, StringComparison.OrdinalIgnoreCase))
        {
            attachment.MimeType = resolved;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* concurrent repair by another request — response MIME is still correct */ }
        }
        return resolved;
    }

    /// Generates and stores a missing thumbnail for an existing raster image attachment.
    /// Returns false when the source cannot be read or decoded, leaving the row untouched.
    // No userId parameter: both the read of the original and the write of the generated thumbnail
    // go through the service account, so the viewer's identity is irrelevant to this work.
    private static async Task<bool> TryBackfillThumbnailAsync(IPopDbContext db, IFileAccessService fileSvc,
        IWebHostEnvironment env, ChatAttachment attachment, CancellationToken ct)
    {
        try
        {
            await using var source = new MemoryStream();
            if (TryLocalFile(env.ContentRootPath, attachment.SynologyServer, attachment.SynologyPath, out var localFile))
            {
                await using var fs = File.OpenRead(localFile);
                await fs.CopyToAsync(source, ct);
            }
            else
            {
                await using var remote = await DownloadAttachmentAsync(fileSvc, attachment.SynologyPath, ct);
                await remote.CopyToAsync(source, ct);
            }

            source.Position = 0;
            var thumbBytes = CreateThumbnail(source, 640);
            var storedName = Path.GetFileNameWithoutExtension(attachment.SynologyPath);
            var thumbName = $"{storedName}.thumb.jpg";

            if (attachment.SynologyServer == "local")
            {
                // SynologyPath is "chat-files/{roomId:N}/{file}" relative to ContentRoot.
                var relDir = Path.GetDirectoryName(attachment.SynologyPath)!.Replace('\\', '/');
                var absDir = Path.Combine(env.ContentRootPath, relDir);
                Directory.CreateDirectory(absDir);
                await File.WriteAllBytesAsync(Path.Combine(absDir, thumbName), thumbBytes, ct);
                attachment.ThumbnailPath = $"{relDir}/{thumbName}";
            }
            else
            {
                var remoteDir = Path.GetDirectoryName(attachment.SynologyPath)!.Replace('\\', '/');
                await using var thumbStream = new MemoryStream(thumbBytes);
                // Written as the service account, matching the reader: a thumbnail stored under one
                // member's home would be exactly as unreadable as the original was.
                await fileSvc.UploadAsServiceAsync(remoteDir, thumbName, thumbStream, ct);
                attachment.ThumbnailPath = $"{remoteDir}/{thumbName}";
            }

            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (SynoException)
        {
            // The source could not be READ (missing credential, wrong home, NAS down). The row is
            // fine — only this caller's access failed. Persisting a fallback here would overwrite a
            // still-valid ThumbnailPath for every other member, so leave the row untouched and let
            // the caller surface the access error instead.
            return false;
        }
        catch (ImageFormatException)
        {
            // The bytes really are not a decodable image: serving the original as its own preview
            // is the correct permanent answer, so it is safe to persist.
            try
            {
                attachment.ThumbnailPath = attachment.SynologyPath;
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch { return false; }
        }
        catch
        {
            // Unknown failure — do not write to the row on a guess.
            return false;
        }
    }

    // ── MIME resolution ───────────────────────────────────────────────────────
    // The browser does not always send a Content-Type for multipart parts (Blazor's
    // StreamContent sends none at all), so IFormFile.ContentType arrives as "".
    // Extension is therefore the authoritative source and the header only a hint.
    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // images
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".jpe"] = "image/jpeg",
        [".png"] = "image/png", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".bmp"] = "image/bmp", [".tif"] = "image/tiff", [".tiff"] = "image/tiff",
        [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon",
        [".heic"] = "image/heic", [".heif"] = "image/heif", [".avif"] = "image/avif",
        // documents
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".dot"] = "application/msword",
        [".dotx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.template",
        [".rtf"] = "application/rtf",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".xlsm"] = "application/vnd.ms-excel.sheet.macroEnabled.12",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",
        // text
        [".txt"] = "text/plain", [".log"] = "text/plain", [".md"] = "text/markdown",
        [".csv"] = "text/csv", [".tsv"] = "text/tab-separated-values",
        [".json"] = "application/json", [".xml"] = "application/xml",
        [".yml"] = "text/yaml", [".yaml"] = "text/yaml",
        [".html"] = "text/html", [".htm"] = "text/html", [".css"] = "text/css",
        // audio / video
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4", [".aac"] = "audio/aac", [".flac"] = "audio/flac",
        [".opus"] = "audio/opus", [".webm"] = "video/webm",
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo", [".mkv"] = "video/x-matroska",
        // archives
        [".zip"] = "application/zip", [".rar"] = "application/vnd.rar",
        [".7z"] = "application/x-7z-compressed", [".tar"] = "application/x-tar",
        [".gz"] = "application/gzip",
    };

    private const string FallbackMime = "application/octet-stream";

    /// Resolves the effective MIME type for a stored file. A concrete, non-generic
    /// header wins; otherwise the extension decides. Never returns null or "".
    internal static string ResolveMime(string? fileName, string? headerContentType)
    {
        var header = headerContentType?.Trim();
        // Strip any "; charset=..." parameter before judging the header.
        if (!string.IsNullOrEmpty(header))
        {
            var semi = header.IndexOf(';');
            if (semi >= 0) header = header[..semi].Trim();
        }

        var ext = Path.GetExtension(fileName ?? "");
        var fromExt = !string.IsNullOrEmpty(ext) && MimeByExtension.TryGetValue(ext, out var mapped) ? mapped : null;

        var headerIsUseful = !string.IsNullOrEmpty(header)
            && !header.Equals(FallbackMime, StringComparison.OrdinalIgnoreCase)
            && header.Contains('/');

        // Chrome reports .csv as application/vnd.ms-excel and .md as empty; when the
        // extension knows better for these text-ish formats, prefer the extension.
        if (headerIsUseful && fromExt is not null && header!.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
            && fromExt.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return fromExt;

        return headerIsUseful ? header! : fromExt ?? FallbackMime;
    }

    /// Any MIME the browser can display as a picture (includes SVG, which has no raster thumb).
    private static bool IsImageMime(string? contentType) =>
        contentType is not null && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// Formats ImageSharp can decode, i.e. the ones we can build a real thumbnail for.
    /// SVG/ICO/HEIC are excluded — no decoder in the ImageSharp build we reference.
    private static bool IsRasterImageMime(string? contentType) => contentType?.ToLowerInvariant() is
        "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/bmp" or "image/tiff";

    /// Reads a chat attachment's bytes off the NAS.
    ///
    /// Always uses the SERVICE account, never the caller's own Synology credential: every member of
    /// a room has to be able to read what one member uploaded, and a per-user session resolves
    /// "/home" — and FileStation permissions — to that one account. Reading as the caller is exactly
    /// why recipients got broken image tiles while the sender saw the picture.
    ///
    /// Rows written before the service account existed may name a different server; they are tried
    /// through the service session anyway, which succeeds when the path is on a share the service
    /// account can see and fails cleanly when it is not.
    internal static Task<Stream> DownloadAttachmentAsync(IFileAccessService fileSvc, string path, CancellationToken ct)
    {
        if (!fileSvc.HasServiceAccount)
            throw new SynoException("No Synology service account is configured on the server.", authFailure: true);
        return fileSvc.DownloadAsServiceAsync(path, ct);
    }

    // ponytail: local disk only when Synology creds are missing; swap to object storage when NAS is required.
    /// An attachment's bytes never change for its id, so let the browser keep them: reopening a
    /// room (or a fresh tab) then serves images from the HTTP cache instead of the NAS.
    /// Set only on success so a transient NAS error is never cached.
    private static IResult CachedFile(HttpContext http, IResult file)
    {
        http.Response.Headers.CacheControl = "private, max-age=604800, immutable";
        return file;
    }

    internal static bool TryLocalFile(string contentRoot, string server, string? relative, out string full)
    {
        full = "";
        if (server is not "local" || string.IsNullOrWhiteSpace(relative)) return false;
        var root = Path.GetFullPath(Path.Combine(contentRoot, "chat-files"));
        full = Path.GetFullPath(Path.Combine(contentRoot, relative));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            full = "";
            return false;
        }
        return true;
    }

    private static string SanitizeFileName(string? name)
    {
        var raw = Path.GetFileName(name ?? "");
        if (string.IsNullOrWhiteSpace(raw)) return "file";
        var cleaned = string.Concat(raw.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return cleaned.Length > 180 ? cleaned[..180] : cleaned;
    }

    private static string EscapeLike(string query) =>
        query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    public const long MaxGroupAvatarBytes = 2 * 1024 * 1024;
    private static readonly string[] GroupAvatarExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];

    /// Null when acceptable; otherwise an error code (no_file / not_an_image / too_large).
    public static string? ValidateGroupAvatar(string? fileName, long length)
    {
        if (string.IsNullOrWhiteSpace(fileName) || length <= 0) return "no_file";
        if (!GroupAvatarExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant())) return "not_an_image";
        if (length > MaxGroupAvatarBytes) return "too_large";
        return null;
    }

    /// Center-crops to a square and scales down to size×size JPEG.
    public static byte[] CreateSquareAvatar(Stream input, int size)
    {
        using var image = Image.Load(input);
        image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(size, size), Mode = ResizeMode.Crop }));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    /// Resolves "/api/chat-avatars/{name}" to its file; null for anything else (no path traversal).
    public static string? GroupAvatarPath(string contentRoot, string? urlOrName)
    {
        if (string.IsNullOrWhiteSpace(urlOrName)) return null;
        var name = urlOrName.StartsWith("/api/chat-avatars/") ? urlOrName["/api/chat-avatars/".Length..] : urlOrName;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[0-9a-f]{32}\\.jpg$")) return null;
        return Path.Combine(contentRoot, "chat-files", "avatars", name);
    }

    private static void DeleteGroupAvatarFile(string contentRoot, string? previousUrl)
    {
        var path = GroupAvatarPath(contentRoot, previousUrl);
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { /* orphan file is harmless */ }
    }

    // ponytail: real thumbnails only for raster images; fallback is the full file path
    private static byte[] CreateThumbnail(Stream input, int maxWidth)
    {
        using var image = Image.Load(input);
        var ratio = (double)maxWidth / image.Width;
        if (image.Width <= maxWidth && image.Height <= maxWidth) ratio = 1;
        var w = (int)Math.Round(image.Width * ratio);
        var h = (int)Math.Round(image.Height * ratio);
        if (ratio < 1) image.Mutate(x => x.Resize(w, h));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms); // JPEG thumb works for all source formats; small + universal
        return ms.ToArray();
    }

    // Request DTOs
    public record DeleteRoomRequest(string? ConfirmName);
    public record CreateDmRequest(Guid RecipientId);
    public record SendMessageBody(string Content, Guid IdempotencyKey, Guid? ReplyToId, string? Type, Guid? RoomId, IReadOnlyList<Guid>? AttachmentIds = null);
    public record EditRequest(string Content);
    public record ForwardRequest(Guid TargetRoomId);
    public record ReactRequest(string Emoji);
    public record PollVoteRequest(int OptionIndex);
    public record MuteRequest(string Duration);
    public record AddMemberRequest(Guid UserId);
    public record ChangeRoleRequest(string Role);
    public record EmergencyRequest(string Content);

    public record ReadReceiptsRequest(bool Enabled);
}
