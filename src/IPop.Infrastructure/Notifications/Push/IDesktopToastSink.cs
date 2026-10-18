namespace IPop.Infrastructure.Notifications.Push;

/// Delivers a toast to the iPop Notifier desktop app of one user (implemented in IPop.Api over the notification hub,
/// event "desktopToast"). Used for chat messages, which have no gateway notification of their own.
public interface IDesktopToastSink
{
    Task SendAsync(Guid userId, PushPayload payload, CancellationToken ct);
}
