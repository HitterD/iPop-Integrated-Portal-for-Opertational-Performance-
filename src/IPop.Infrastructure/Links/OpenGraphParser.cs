using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace IPop.Infrastructure.Links;

public sealed record OpenGraphData(string? Title, string? Description, string? SiteName, Uri? Image)
{
    public bool IsUseful => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Description);
}

/// Pulls the WhatsApp-style card out of a page: og:* first, then twitter:*, then &lt;title&gt; /
/// meta description. Text is entity-decoded by the parser, whitespace-collapsed and trimmed.
public static partial class OpenGraphParser
{
    public const int MaxTitle = 200;
    public const int MaxDescription = 300;
    public const int MaxSiteName = 100;

    static OpenGraphParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static OpenGraphData Parse(byte[] body, string? headerCharset, Uri pageUri) =>
        Parse(Decode(body, headerCharset), pageUri);

    public static OpenGraphData Parse(string html, Uri pageUri)
    {
        var doc = new HtmlParser(new HtmlParserOptions { IsScripting = false }).ParseDocument(html);

        string? Meta(params string[] keys)
        {
            foreach (var key in keys)
            {
                foreach (var m in doc.QuerySelectorAll("meta"))
                {
                    var name = m.GetAttribute("property") ?? m.GetAttribute("name") ?? m.GetAttribute("itemprop");
                    if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
                    var v = Clean(m.GetAttribute("content"));
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            return null;
        }

        var title = Meta("og:title", "twitter:title") ?? Clean(doc.QuerySelector("title")?.TextContent);
        var desc = Meta("og:description", "twitter:description", "description");
        var site = Meta("og:site_name", "application-name");
        var imageRaw = Meta("og:image:secure_url", "og:image", "og:image:url", "twitter:image", "twitter:image:src");

        // <base href> moves relative URLs; honour it only if it is itself http(s).
        var baseUri = pageUri;
        var baseHref = doc.QuerySelector("base[href]")?.GetAttribute("href");
        if (!string.IsNullOrWhiteSpace(baseHref) && Uri.TryCreate(pageUri, baseHref.Trim(), out var b)
            && (b.Scheme == Uri.UriSchemeHttp || b.Scheme == Uri.UriSchemeHttps))
            baseUri = b;

        Uri? image = null;
        if (imageRaw is not null && Uri.TryCreate(baseUri, imageRaw, out var img)
            && (img.Scheme == Uri.UriSchemeHttp || img.Scheme == Uri.UriSchemeHttps))
            image = img;

        return new OpenGraphData(Trim(title, MaxTitle), Trim(desc, MaxDescription), Trim(site, MaxSiteName), image);
    }

    /// Header charset wins, then &lt;meta charset&gt; / http-equiv in the first 4 KB, then BOM/UTF-8.
    public static string Decode(byte[] body, string? headerCharset)
    {
        var enc = TryEncoding(headerCharset);
        if (enc is null)
        {
            var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 4096));
            var m = MetaCharset().Match(head);
            if (m.Success) enc = TryEncoding(m.Groups[1].Value);
        }
        enc ??= Encoding.UTF8;
        using var reader = new StreamReader(new MemoryStream(body), enc, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static Encoding? TryEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try { return Encoding.GetEncoding(name.Trim().Trim('"', '\'')); }
        catch (ArgumentException) { return null; }
    }

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Ws();

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // AngleSharp already decoded entities in attributes/text; a second pass catches pages
        // that double-encode ("&amp;amp;") which is common in og tags.
        var v = Ws().Replace(System.Net.WebUtility.HtmlDecode(s), " ").Trim();
        return v.Length == 0 ? null : v;
    }

    private static string? Trim(string? s, int max)
    {
        if (s is null) return null;
        if (s.Length <= max) return s;
        var cut = s[..(max - 1)];
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut.TrimEnd() + "…";
    }
}
