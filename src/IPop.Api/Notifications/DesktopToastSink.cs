using IPop.Infrastructure.Notifications.Push;
using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Notifications;

/// Chat toasts for the iPop Notifier app: same payload as the browser toast, sent to the user's hub connections.
public sealed class DesktopToastSink(IHubContext<NotificationHub> hub) : IDesktopToastSink
{
    public Task SendAsync(Guid userId, PushPayload payload, CancellationToken ct) =>
        hub.Clients.User(userId.ToString()).SendAsync("desktopToast", payload, ct);

    public Task TicketChatReadAsync(Guid userId, IReadOnlyList<string> ticketRefs, CancellationToken ct) =>
        hub.Clients.User(userId.ToString()).SendAsync("ticketChatRead", ticketRefs, ct);
}
