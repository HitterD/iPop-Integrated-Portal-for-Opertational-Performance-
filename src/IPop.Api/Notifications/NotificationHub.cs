using IPop.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Notifications;

/// Thin broadcast shell — same contract as PresenceHub: the transport is a detail;
/// all sends go through NotificationBroadcaster so endpoints/services never touch
/// Hub types. Connections also join their user group so "notificationsChanged"
/// (read/unread/delete elsewhere) reaches the iPop Notifier, which only uses this hub.
public class NotificationHub : Microsoft.AspNetCore.SignalR.Hub
{
    public override async Task OnConnectedAsync()
    {
        var nik = Context.User?.FindFirst("nik")?.Value;
        if (!string.IsNullOrEmpty(nik))
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeEvents.UserGroup(nik));
        await base.OnConnectedAsync();
    }
}
