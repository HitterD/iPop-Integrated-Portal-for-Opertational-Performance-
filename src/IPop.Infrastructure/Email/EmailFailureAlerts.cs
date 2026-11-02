using IPop.Application.Notifications;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Email;

/// Tells IT admins when iPop can't send mail (forgot-password links, etc.) — at most once
/// every 6 hours per kind of problem, so a broken mailbox password can't go unnoticed for days.
/// Users never see this: the forgot-password screen answers the same either way.
public sealed class EmailFailureAlerts(IServiceScopeFactory scopes, ILogger<EmailFailureAlerts> log)
{
    internal static readonly TimeSpan Quiet = TimeSpan.FromHours(6);
    private readonly Dictionary<string, DateTime> _last = [];
    private readonly Lock _gate = new();

    public sealed record Alert(string Key, string Title, string Message);

    /// Pure so it can be tested.
    public static Alert Describe(Exception ex) => ex switch
    {
        AuthenticationException or MailKit.Net.Smtp.SmtpCommandException { StatusCode: (MailKit.Net.Smtp.SmtpStatusCode)535 } =>
            new("smtp-auth", "Email can't be sent: wrong mailbox password",
                "The mail server rejected iPop's sign-in, so password reset emails are not going out. Update the password in Admin › Email."),
        _ => new("smtp-other", "Email can't be sent",
                $"iPop could not send email: {ex.Message}. Check Admin › Email."),
    };

    public async Task ReportAsync(Exception ex, DateTime nowUtc, CancellationToken ct)
    {
        var alert = Describe(ex);
        lock (_gate)
        {
            if (_last.TryGetValue(alert.Key, out var at) && nowUtc - at < Quiet) return;
            _last[alert.Key] = nowUtc;
        }
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<INotificationGatewayService>()
                .PublishInternalAsync("system", "system.ops", alert.Title, alert.Message,
                    new NotificationTarget(Role: "Admin"), "high", "/admin/email", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Could not alert admins about email failure");
        }
    }
}
