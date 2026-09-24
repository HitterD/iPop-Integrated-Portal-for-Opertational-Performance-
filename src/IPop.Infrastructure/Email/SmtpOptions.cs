using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Email;

/// Bound from the "Smtp" config section. An empty Host means dev: mail is logged, not sent.
public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    /// TLS from the first byte (SMTPS, usually port 465). Defaults on for port 465.
    public bool? ImplicitTls { get; set; }
    /// Accept an untrusted/self-signed server certificate. Internal mail servers only.
    public bool AllowSelfSignedCert { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "ipop@sja.co.id";
    public string FromName { get; set; } = "iPop";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
    public bool UsesImplicitTls => ImplicitTls ?? Port == 465;

    public static SmtpOptions Bind(IConfiguration cfg) =>
        cfg.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
}
