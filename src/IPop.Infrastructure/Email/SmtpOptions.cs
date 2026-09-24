using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Email;

/// Bound from the "Smtp" config section. An empty Host means dev: mail is logged, not sent.
public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "ipop@sja.co.id";
    public string FromName { get; set; } = "iPop";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public static SmtpOptions Bind(IConfiguration cfg) =>
        cfg.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
}
