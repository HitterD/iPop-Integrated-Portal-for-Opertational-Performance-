using IPop.Application.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace IPop.Infrastructure.Email;

/// Sends with the settings saved in Admin › Email, falling back to appsettings "Smtp".
/// With no host configured anywhere (dev) mail is only logged.
public sealed class SmtpEmailSender(SmtpSettingsStore store, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(EmailMessage msg, CancellationToken ct = default)
    {
        var (opt, _) = await store.EffectiveAsync(ct);
        if (!opt.IsConfigured)
        {
            log.LogInformation("Email (not sent, SMTP not configured) to {To}: {Subject}",
                string.Join(", ", msg.To), msg.Subject);
            return;
        }
        await SendWithAsync(opt, msg, ct);
    }

    public static async Task SendWithAsync(SmtpOptions opt, EmailMessage msg, CancellationToken ct)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(opt.FromName, opt.From));
        foreach (var to in msg.To) mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = msg.Subject;
        mime.Body = new BodyBuilder { HtmlBody = msg.HtmlBody, TextBody = msg.TextBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 20_000 };
        if (opt.AllowSelfSignedCert) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var security = opt.UsesImplicitTls ? SecureSocketOptions.SslOnConnect
            : opt.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(opt.Host!, opt.Port, security, ct);
        if (!string.IsNullOrEmpty(opt.User))
            await client.AuthenticateAsync(opt.User, opt.Password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
