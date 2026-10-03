using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Links;

/// Hosts that mean "this iPop" — links to them are answered from the database, never fetched.
/// Bound from LinkPreview:InternalHosts plus the hosts of Cors:WebOrigins; ipop.santos.co.id is
/// always included.
public sealed class LinkPreviewOptions
{
    public const string DefaultPublicHost = "ipop.santos.co.id";

    public HashSet<string> InternalHosts { get; } = new(StringComparer.OrdinalIgnoreCase) { DefaultPublicHost };

    public TimeSpan PositiveTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan NegativeTtl { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan InternalTtl { get; init; } = TimeSpan.FromMinutes(2);
    /// Lifetime of a signed image URL; expiry is rounded up to the hour so URLs stay stable
    /// (and browser-cacheable) for a while.
    public TimeSpan ImageUrlLifetime { get; init; } = TimeSpan.FromHours(12);

    public const int MaxHtmlBytes = 1024 * 1024;
    public const int MaxImageBytes = 3 * 1024 * 1024;
    public const int MaxImageWidth = 600;

    public static LinkPreviewOptions Bind(IConfiguration cfg)
    {
        var o = new LinkPreviewOptions();
        foreach (var h in cfg.GetSection("LinkPreview:InternalHosts").Get<string[]>() ?? [])
            if (!string.IsNullOrWhiteSpace(h)) o.InternalHosts.Add(h.Trim());
        foreach (var origin in cfg.GetSection("Cors:WebOrigins").Get<string[]>() ?? [])
            if (Uri.TryCreate(origin, UriKind.Absolute, out var u)) o.InternalHosts.Add(u.Host);
        return o;
    }
}
