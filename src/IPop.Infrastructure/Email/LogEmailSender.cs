using IPop.Application.Email;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Email;

/// Dev transport when Smtp:Host is empty. Logs recipients and subject only — bodies can carry
/// reset tokens, which must not end up in log files.
public sealed class LogEmailSender(ILogger<LogEmailSender> log) : IEmailSender
{
    public Task SendAsync(EmailMessage msg, CancellationToken ct = default)
    {
        log.LogInformation("Email (not sent, SMTP not configured) to {To}: {Subject}",
            string.Join(", ", msg.To), msg.Subject);
        return Task.CompletedTask;
    }
}
