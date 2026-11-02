using IPop.Application.Email;
using IPop.Infrastructure.Email;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Admin › Email: the outgoing mailbox iPop sends from (password resets, mass mail notices…).
/// The password is write-only — GET only says whether one is set.
public static class EmailSettingsEndpoints
{
    public sealed record EmailSettingsDto(string? Host, int Port, string Security, bool AllowSelfSignedCert,
        string? User, bool HasPassword, string From, string FromName, string Source,
        EmailHealthDto Health);
    public sealed record EmailHealthDto(DateTime? LastSentAt, DateTime? LastFailedAt, string? LastError, int Stuck);
    public sealed record SaveEmailSettingsRequest(string? Host, int Port, string? Security, bool AllowSelfSignedCert,
        string? User, string? Password, string? From, string? FromName);
    public sealed record TestEmailRequest(SaveEmailSettingsRequest Settings, string? To);

    public static IEndpointRouteBuilder MapEmailSettings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/email").RequireAuthorization("AdminOnly");

        g.MapGet("", async (SmtpSettingsStore store, IPopDbContext db, CancellationToken ct) =>
        {
            var (o, fromAdmin) = await store.EffectiveAsync(ct);
            return Results.Ok(new EmailSettingsDto(o.Host, o.Port, SecurityOf(o), o.AllowSelfSignedCert, o.User,
                !string.IsNullOrEmpty(o.Password), o.From, o.FromName, fromAdmin ? "admin" : "file",
                await HealthAsync(db, ct)));
        });

        g.MapPut("", async (SaveEmailSettingsRequest req, SmtpSettingsStore store, HttpContext http, CancellationToken ct) =>
        {
            if (Validate(req) is { } bad) return bad;
            var merged = await store.MergeAsync(ToOptions(req), ct);
            await store.SaveAsync(merged, http.User.FindFirst("nik")?.Value, ct);
            return Results.NoContent();
        });

        // Sends with the settings on screen (saved or not), so the admin can try a new password first.
        g.MapPost("/test", async (TestEmailRequest req, SmtpSettingsStore store, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            if (Validate(req.Settings) is { } bad) return bad;
            var to = req.To?.Trim();
            if (string.IsNullOrEmpty(to))
            {
                var nik = http.User.FindFirst("nik")?.Value;
                to = await db.Users.Where(u => u.Nik == nik).Select(u => u.Email).FirstOrDefaultAsync(ct);
            }
            if (string.IsNullOrWhiteSpace(to) || !MimeKit.MailboxAddress.TryParse(to, out _))
                return Results.BadRequest(new { code = "no_recipient", message = "Enter an email address to send the test to." });

            var opt = await store.MergeAsync(ToOptions(req.Settings), ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                await SmtpEmailSender.SendWithAsync(opt, new EmailMessage([to], "iPop test email",
                    "<p>This is a test from iPop Admin › Email. If you can read this, password reset emails will arrive too.</p>",
                    "This is a test from iPop Admin › Email. If you can read this, password reset emails will arrive too."), cts.Token);
                return Results.Ok(new { ok = true, to });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Results.Ok(new { ok = false, to, message = Friendly(ex) });
            }
        });

        return app;
    }

    private static async Task<EmailHealthDto> HealthAsync(IPopDbContext db, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-7);
        var lastSent = await db.EmailOutbox.Where(m => m.SentAt != null).MaxAsync(m => (DateTime?)m.SentAt, ct);
        var lastFail = await db.EmailOutbox.Where(m => m.SentAt == null && m.LastError != null)
            .OrderByDescending(m => m.CreatedAt).Select(m => new { m.CreatedAt, m.LastError }).FirstOrDefaultAsync(ct);
        var stuck = await db.EmailOutbox.CountAsync(m => m.SentAt == null
            && m.Attempts >= IPop.Core.Entities.EmailOutboxMessage.MaxAttempts && m.CreatedAt > since, ct);
        // Only report a failure newer than the last success — otherwise it's already fixed.
        var failed = lastFail is not null && (lastSent is null || lastFail.CreatedAt > lastSent);
        return new EmailHealthDto(lastSent, failed ? lastFail!.CreatedAt : null, failed ? lastFail!.LastError : null, stuck);
    }

    internal static string SecurityOf(SmtpOptions o) => o.UsesImplicitTls ? "ssl" : o.UseStartTls ? "starttls" : "none";

    internal static SmtpOptions ToOptions(SaveEmailSettingsRequest r) => new()
    {
        Host = r.Host?.Trim(), Port = r.Port,
        ImplicitTls = r.Security == "ssl", UseStartTls = r.Security == "starttls",
        AllowSelfSignedCert = r.AllowSelfSignedCert,
        User = string.IsNullOrWhiteSpace(r.User) ? null : r.User.Trim(),
        Password = string.IsNullOrEmpty(r.Password) ? null : r.Password,
        From = (r.From ?? "").Trim(), FromName = string.IsNullOrWhiteSpace(r.FromName) ? "iPop" : r.FromName.Trim(),
    };

    private static IResult? Validate(SaveEmailSettingsRequest r)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(r.Host)) errors["host"] = "Enter the mail server.";
        if (r.Port is < 1 or > 65535) errors["port"] = "Use a port from 1 to 65535.";
        if (r.Security is not ("ssl" or "starttls" or "none")) errors["security"] = "Choose a security option.";
        if (string.IsNullOrWhiteSpace(r.From) || !MimeKit.MailboxAddress.TryParse(r.From.Trim(), out _))
            errors["from"] = "Enter a valid sender address.";
        return errors.Count == 0 ? null
            : Results.BadRequest(new { code = "invalid", message = "Check the highlighted fields.", fields = errors });
    }

    internal static string Friendly(Exception ex) => ex switch
    {
        MailKit.Security.AuthenticationException => "The mail server rejected the user name or password.",
        MailKit.Net.Smtp.SmtpCommandException { StatusCode: (MailKit.Net.Smtp.SmtpStatusCode)535 } => "The mail server rejected the user name or password.",
        MailKit.Security.SslHandshakeException => "Secure connection failed. Check the port and security option, or the server certificate.",
        System.Net.Sockets.SocketException => "Could not reach the mail server. Check the server name and port.",
        OperationCanceledException => "The mail server did not answer in time.",
        _ => ex.Message,
    };
}
