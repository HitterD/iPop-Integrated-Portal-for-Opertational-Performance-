namespace IPop.Application.Chat;

public interface INotificationGateway
{
    Task SendMentionNotificationAsync(Guid recipientId, string senderName, string roomName, string preview, bool isHighPriority, CancellationToken ct, Guid? messageId = null);
    Task SendEmergencyNotificationAsync(IReadOnlyList<Guid> recipientIds, string senderName, string roomName, string content, CancellationToken ct);
}
