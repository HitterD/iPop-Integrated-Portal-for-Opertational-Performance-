using IPop.Application.Links;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Links;

/// Bounded cache shared by previews and resized images. Entry Size is in KB; 64 MB total.
public sealed class LinkPreviewCache() : MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 });

/// GET /api/link-preview. External links: fetched server-side through <see cref="SafeHttpFetcher"/>,
/// parsed for OpenGraph, cached by normalized URL (24 h, failures 1 h) and shared by all users.
/// Internal (iPop) links: never fetched; resolved per viewer and cached per viewer only briefly.
public sealed class LinkPreviewService(
    LinkPreviewCache cache,
    SafeHttpFetcher fetcher,
    LinkPreviewOptions options,
    LinkPreviewImageSigner signer,
    InternalLinkResolver internalLinks,
    ILogger<LinkPreviewService> log)
{
    private sealed record ExternalEntry(OpenGraphData? Data);

    /// Null = nothing to show (bad/blocked/failed/no useful metadata) → endpoint answers 204.
    public async Task<LinkPreviewDto?> GetAsync(string? rawUrl, Guid viewerId, CancellationToken ct)
    {
        var uri = Normalize(rawUrl);
        if (uri is null) return null;

        if (IsInternal(uri))
        {
            var key = $"lp:int:{viewerId:N}:{uri.PathAndQuery}";
            if (!cache.TryGetValue(key, out InternalLinkDto? card) || card is null)
            {
                card = await internalLinks.ResolveAsync(uri, viewerId, ct);
                cache.Set(key, card, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = options.InternalTtl });
            }
            return new LinkPreviewDto(uri.AbsoluteUri, LinkPreviewKinds.Internal, card.Title, card.Subtitle, "iPop", null, card);
        }

        var extKey = "lp:ext:" + uri.AbsoluteUri;
        if (!cache.TryGetValue(extKey, out ExternalEntry? entry) || entry is null)
        {
            entry = await FetchExternalAsync(uri, ct);
            cache.Set(extKey, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = entry.Data is null ? options.NegativeTtl : options.PositiveTtl });
        }
        if (entry.Data is not { } og) return null;

        return new LinkPreviewDto(uri.AbsoluteUri, LinkPreviewKinds.External, og.Title, og.Description,
            og.SiteName ?? uri.IdnHost, og.Image is null ? null : signer.CreateUrl(og.Image, options.ImageUrlLifetime), null);
    }

    private async Task<ExternalEntry> FetchExternalAsync(Uri uri, CancellationToken ct)
    {
        var res = await fetcher.FetchAsync(new FetchRequest(uri,
            m => m is "text/html" or "application/xhtml+xml",
            LinkPreviewOptions.MaxHtmlBytes, AllowTruncate: true,
            Accept: "text/html,application/xhtml+xml;q=0.9,*/*;q=0.1"), ct);
        if (!res.Ok)
        {
            // Host + outcome only: full URLs can carry tokens in their query strings.
            log.LogInformation("Link preview {Host}: {Outcome}", uri.IdnHost, res.Outcome);
            return new ExternalEntry(null);
        }
        var og = OpenGraphParser.Parse(res.Body!, res.Charset, res.FinalUri ?? uri);
        log.LogInformation("Link preview {Host}: {Outcome}", uri.IdnHost, og.IsUseful ? "ok" : "empty");
        return new ExternalEntry(og.IsUseful ? og : null);
    }

    /// Absolute http(s) URL (or an app-relative path, which is internal by definition), without
    /// fragment; host lowercased and default port dropped by <see cref="Uri"/>.
    public Uri? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 2048) return null;
        raw = raw.Trim();
        if (raw.StartsWith('/') && !raw.StartsWith("//"))
            raw = "https://" + LinkPreviewOptions.DefaultPublicHost + raw;
        else if (raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
        var b = new UriBuilder(uri) { Fragment = "" };
        return b.Uri;
    }

    public bool IsInternal(Uri uri) => options.InternalHosts.Contains(uri.IdnHost);
}
