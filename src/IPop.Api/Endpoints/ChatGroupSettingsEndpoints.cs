using IPop.Api.Rbac;
using IPop.Application.Chat;
using IPop.Application.Realtime;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Group settings that live outside the big ChatEndpoints file.
public static class ChatGroupSettingsEndpoints
{
    public static IEndpointRouteBuilder MapChatGroupSettings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat").RequireAuthorization().RequirePermission("chat.read");

        // PUT /api/chat/rooms/{roomId}/mention-all — { "allowed": bool }, owner/admin only.
        // Whether ordinary members may use @all in this group (default on). Owners/admins always may.
        g.MapPut("/rooms/{roomId:guid}/mention-all", async (Guid roomId, SetMemberMentionAllRequest req,
            HttpContext http, IChatService svc, IChatNotifier notifier, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = IPop.Api.Auth.CurrentUser.IdFromToken(http.User)
                ?? (http.User.FindFirst("nik")?.Value is { Length: > 0 } nik
                    ? (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik, ct))?.Id : null);
            if (userId is null) return Results.Unauthorized();

            var r = await svc.SetMemberMentionAllAsync(userId.Value, roomId, req.Allowed, ct);
            if (!r.Success)
            {
                var forbidden = r.Error == "forbidden";
                return Results.Json(new { code = r.Error ?? "forbidden", message = r.Error }, statusCode: forbidden ? 403 : 422);
            }
            // Open clients reload the room (and its flag) on this event.
            var room = await db.ChatRooms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == roomId, ct);
            if (room is not null)
                await notifier.NotifyRoomUpdatedAsync(roomId, new RoomUpdateDto(roomId, room.Name, room.LastMessageAt), ct);
            return Results.Ok(r);
        }).RequirePermission("chat.send");

        return app;
    }
}
