namespace IPop.Application.Email;

public record EmailMessage(IReadOnlyList<string> To, string Subject, string HtmlBody, string? TextBody = null);

/// Transport only — callers enqueue through IEmailQueue so a slow SMTP never blocks a request.
public interface IEmailSender
{
    Task SendAsync(EmailMessage msg, CancellationToken ct = default);
}

public interface IEmailQueue
{
    Task EnqueueAsync(EmailMessage msg, CancellationToken ct = default);
}
