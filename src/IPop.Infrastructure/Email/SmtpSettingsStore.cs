using System.Text.Json;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Email;

/// SMTP settings an admin saved in Admin › Email. They override the "Smtp" section of
/// appsettings so a rotated mailbox password can be fixed without a restart. The password
/// is encrypted with Data Protection (keys live in the DB) and never leaves the server.
public sealed class SmtpSettingsStore(IPopDbContext db, SmtpOptions fileOptions, IDataProtectionProvider dp)
{
    public const string Key = "smtp.config";
    private const string Purpose = "IPop.Smtp.Password.v1";

    /// What is stored in AppSettings.ValueJson. PasswordProtected is the encrypted password.
    public sealed record Stored(string? Host, int Port, bool? ImplicitTls, bool UseStartTls, bool AllowSelfSignedCert,
        string? User, string? PasswordProtected, string From, string FromName);

    private IDataProtector Protector => dp.CreateProtector(Purpose);

    /// The settings used to send right now, and whether they come from Admin or the config file.
    public async Task<(SmtpOptions Options, bool FromAdmin)> EffectiveAsync(CancellationToken ct = default)
    {
        var stored = await LoadAsync(ct);
        return stored is null ? (fileOptions, false) : (ToOptions(stored), true);
    }

    public async Task<Stored?> LoadAsync(CancellationToken ct = default)
    {
        var json = await db.AppSettings.AsNoTracking().Where(s => s.Key == Key)
            .Select(s => s.ValueJson).FirstOrDefaultAsync(ct);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<Stored>(json); }
        catch (JsonException) { return null; }
    }

    public SmtpOptions ToOptions(Stored s)
    {
        string? password = null;
        if (!string.IsNullOrEmpty(s.PasswordProtected))
        {
            try { password = Protector.Unprotect(s.PasswordProtected); }
            catch (System.Security.Cryptography.CryptographicException) { password = null; }
        }
        return new SmtpOptions
        {
            Host = s.Host, Port = s.Port, ImplicitTls = s.ImplicitTls, UseStartTls = s.UseStartTls,
            AllowSelfSignedCert = s.AllowSelfSignedCert, User = s.User, Password = password,
            From = s.From, FromName = s.FromName,
        };
    }

    /// Settings from the form. A null/empty password keeps the current one (Admin or file).
    public async Task<SmtpOptions> MergeAsync(SmtpOptions form, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(form.Password)) return form;
        var (current, _) = await EffectiveAsync(ct);
        return new SmtpOptions
        {
            Host = form.Host, Port = form.Port, ImplicitTls = form.ImplicitTls, UseStartTls = form.UseStartTls,
            AllowSelfSignedCert = form.AllowSelfSignedCert, User = form.User, Password = current.Password,
            From = form.From, FromName = form.FromName,
        };
    }

    /// Saves (already merged) settings. Audited without the password.
    public async Task SaveAsync(SmtpOptions o, string? actorNik, CancellationToken ct = default)
    {
        var stored = new Stored(o.Host?.Trim(), o.Port, o.ImplicitTls, o.UseStartTls, o.AllowSelfSignedCert,
            string.IsNullOrWhiteSpace(o.User) ? null : o.User.Trim(),
            string.IsNullOrEmpty(o.Password) ? null : Protector.Protect(o.Password),
            o.From.Trim(), o.FromName.Trim());
        var now = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(stored);
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null) db.AppSettings.Add(row = new IPop.Core.Entities.AppSetting { Key = Key, ValueJson = json });
        row.ValueJson = json;
        row.UpdatedAt = now;
        row.UpdatedByNik = actorNik;
        db.AuditLogs.Add(new IPop.Core.Entities.AuditLog
        {
            ActorNik = actorNik, Action = "settings.smtp_changed", Entity = "AppSetting", EntityId = Key,
            DetailJson = JsonSerializer.Serialize(new { stored.Host, stored.Port, stored.User, stored.From, stored.FromName }),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }
}
