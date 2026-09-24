using IPop.Application.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace IPop.Infrastructure.Email;

public sealed class SmtpEmailSender(SmtpOptions opt) : IEmailSender
{
    public async Task SendAsync(EmailMessage msg, CancellationToken ct = default)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(opt.FromName, opt.From));
        foreach (var to in msg.To) mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = msg.Subject;
        mime.Body = new BodyBuilder { HtmlBody = msg.HtmlBody, TextBody = msg.TextBody }.ToMessageBody();

        using var client = new SmtpClient();
        if (opt.AllowSelfSignedCert) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var security = opt.UsesImplicitTls ? SecureSocketOptions.SslOnConnect
            : opt.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(opt.Host!, opt.Port, security, ct);   // only registered when IsConfigured
        if (!string.IsNullOrEmpty(opt.User))
            await client.AuthenticateAsync(opt.User, opt.Password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}
