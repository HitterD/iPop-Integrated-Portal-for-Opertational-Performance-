using System.Text.RegularExpressions;
using Ganss.Xss;

namespace IPop.Infrastructure.News;

/// <summary>
/// News bodies come from the rich composer and are rendered as raw markup by the client, so every
/// stored and served body goes through this allow-list first.
///
/// <para>This is deliberately stricter than <c>MailHtml</c>: it does not allow <c>data:</c> URIs
/// (Mail permits inline base64 images) and it does not allow arbitrary iframes. Media is referenced
/// by canonical id only — <c>data-news-media="&lt;guid&gt;"</c> — and the delivery layer resolves a
/// short-lived, access-checked URL at render time, so a stored body can never carry an expiring
/// ticket or a hardcoded token.</para>
///
/// <para>External markup is limited to a strictly parsed YouTube id and a direct HTTPS mp4/webm
/// URL. Neither is ever emitted by the server as caller-supplied HTML.</para>
/// </summary>
public static class NewsHtml
{
    public const string BodyFormatPlain = "plain";
    public const string BodyFormatHtml = "html";

    // Canonical markup attributes. Internal media never persists src/srcset.
    public const string MediaAttribute = "data-news-media";
    public const string YoutubeAttribute = "data-news-youtube";
    public const string VideoUrlAttribute = "data-news-video-url";

    /// Cap for a rich body, mirrored by NewsComposerService so autosave and promotion agree.
    public const int MaxBodyChars = 20000;

    private static readonly HtmlSanitizer Sanitizer = Create();

    private static readonly Regex YoutubeId = new(
        @"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Only these two containers are accepted for browser-native playback. Signed query strings are
    // allowed (the policy is about the scheme/credentials/extension, not about a token), but the
    // value is still never fetched by the server.
    private static readonly string[] DirectVideoExtensions = [".mp4", ".webm"];

    private static HtmlSanitizer Create()
    {
        var s = new HtmlSanitizer();

        // The library's defaults are permissive (form/input/button/select, style, src, a huge CSS
        // set). News builds an explicit allow-list instead of extending them: clear first, then add.
        s.AllowedTags.Clear();
        s.AllowedAttributes.Clear();
        s.AllowedCssProperties.Clear();

        // No data: URIs (paste/drop uploads instead), no mailto in a corporate feed body.
        s.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https" }) s.AllowedSchemes.Add(scheme);

        // Structure and inline formatting only. No form/input/button/select/iframe/object/embed/svg.
        foreach (var t in new[]
                 {
                     "p", "br", "hr", "div", "span", "blockquote", "pre", "code",
                     "h1", "h2", "h3", "h4", "h5", "h6",
                     "b", "strong", "i", "em", "u", "s", "strike", "del", "ins", "sub", "sup", "mark", "small",
                     "ul", "ol", "li", "a", "img",
                     "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "colgroup", "col",
                     "figure", "figcaption",
                 })
            s.AllowedTags.Add(t);

        // class is needed for formatting (Jodit emits classes); style is kept but only the CSS
        // properties in the allow-list below survive (the library sanitizes the declarations);
        // the rest are the canonical ids.
        foreach (var a in new[] { "class", "style", "colspan", "rowspan", "href", "title", "alt", MediaAttribute, YoutubeAttribute, VideoUrlAttribute })
            s.AllowedAttributes.Add(a);

        // Office-grade inline formatting, minus anything that can pull a remote resource
        // (background-image / list-style-image in url() form are simply not on the list).
        foreach (var p in new[]
                 {
                     "line-height", "margin-left", "margin-top", "margin-bottom", "padding-left",
                     "vertical-align", "text-decoration", "border-collapse", "width", "height",
                     "font-family", "font-size", "color", "background-color", "text-align",
                 })
            s.AllowedCssProperties.Add(p);

        // URL-free by construction: internal media is referenced by id only, so any src/srcset/poster
        // on an image or video element is dropped rather than rewritten. On links, the cleared scheme
        // list already restricts to http/https.
        s.FilterUrl += (_, e) =>
        {
            if (e.Tag.TagName.Equals("IMG", StringComparison.OrdinalIgnoreCase)
                || e.Tag.TagName.Equals("VIDEO", StringComparison.OrdinalIgnoreCase))
                e.SanitizedUrl = null;
        };

        // Canonical id attributes must be well-formed or they are dropped entirely, so a malformed
        // value can never reach the renderer.
        s.PostProcessNode += (_, e) =>
        {
            var el = e.Node as AngleSharp.Dom.IElement;
            if (el is null) return;
            DropUnlessValid(el, MediaAttribute, IsMediaId);
            DropUnlessValid(el, YoutubeAttribute, v => YoutubeId.IsMatch(v));
            DropUnlessValid(el, VideoUrlAttribute, IsDirectVideoUrl);
        };

        return s;
    }

    private static void DropUnlessValid(AngleSharp.Dom.IElement el, string attribute, Func<string, bool> valid)
    {
        var value = el.GetAttribute(attribute);
        if (value is null) return;
        if (!valid(value.Trim())) el.RemoveAttribute(attribute);
    }

    private static bool IsMediaId(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty;

    /// <summary>
    /// Absolute https URL with no embedded credentials whose path ends in an approved video
    /// extension. A query string is permitted (signed/CDN links are legitimate); the fragment and
    /// any user-info are not.
    /// </summary>
    public static bool IsDirectVideoUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 2000) return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var ext = Path.GetExtension(uri.AbsolutePath);
        return DirectVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// Sanitize a rich body. Empty/whitespace html returns ""; oversized html is refused by returning ""
    /// so a caller cannot store a snapshot promotion could not write.
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        if (html.Length > MaxBodyChars) return "";
        return Sanitizer.Sanitize(html);
    }

    /// Render a stored body for the reader. "plain" is escaped and preserved verbatim; "html" goes
    /// through the allow-list. Legacy rows (BodyFormat unset/"plain") keep their exact old output.
    public static string Render(string? body, string? bodyFormat)
    {
        if (string.IsNullOrEmpty(body)) return "";
        return string.Equals(bodyFormat, BodyFormatHtml, StringComparison.OrdinalIgnoreCase)
            ? Sanitize(body)
            : System.Net.WebUtility.HtmlEncode(body);
    }

    /// Plain-body preview parity: the reader renders plain bodies escaped inside a pre-wrap block, so
    /// the composer preview escapes the text and turns line breaks into &lt;br&gt; to look the same.
    /// Literal markup in a plain body is shown as text, never interpreted.
    public static string RenderPlainForPreview(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var escaped = System.Net.WebUtility.HtmlEncode(body);
        return escaped.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br>");
    }

    /// Plain-text derivation for feed summaries and notifications. Never returns raw markup, so a
    /// restricted body's markup cannot leak into a notification snippet.
    public static string ToPlainText(string? body, string? bodyFormat)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var text = string.Equals(bodyFormat, BodyFormatHtml, StringComparison.OrdinalIgnoreCase)
            ? Regex.Replace(body, "<[^>]+>", " ")
            : body;
        var decoded = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    /// True when the body carries any canonical internal media reference.
    public static bool ReferencesMedia(string? body) =>
        !string.IsNullOrEmpty(body) && body.Contains(MediaAttribute, StringComparison.OrdinalIgnoreCase);

    /// Internal media ids referenced by a body, for reference-aware cleanup and for checking that the
    /// markup agrees with the draft's normalized references. Parsed from the DOM rather than by regex
    /// so it sees exactly what the renderer sees (attribute order, quoting, entities).
    public static IReadOnlyList<Guid> ReferencedMediaIds(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];
        var ids = new HashSet<Guid>();
        var doc = Parser.ParseDocument(body!);
        foreach (var el in doc.QuerySelectorAll("*"))
        {
            var value = el.GetAttribute(MediaAttribute);
            if (value is not null && Guid.TryParse(value.Trim(), out var id)) ids.Add(id);
        }
        return ids.ToList();
    }

    private static readonly AngleSharp.Html.Parser.HtmlParser Parser = new();

    /// Parse an approved YouTube id out of a user-supplied string (bare id, youtu.be, watch?v=,
    /// embed/, shorts/). Returns null when it is not a single recognisable video id — the caller
    /// then never emits an iframe.
    public static string? ParseYoutubeId(string? input)
    {
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (YoutubeId.IsMatch(value)) return value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtube-nocookie.com" or "www.youtube-nocookie.com" or "youtu.be"))
            return null;

        if (host == "youtu.be")
        {
            var seg = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
            return seg is not null && YoutubeId.IsMatch(seg) ? seg : null;
        }
        if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
        {
            var v = QueryValue(uri.Query, "v");
            return v is not null && YoutubeId.IsMatch(v) ? v : null;
        }
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0] is "embed" or "shorts" or "live" && YoutubeId.IsMatch(parts[1]))
            return parts[1];
        return null;
    }

    /// Trusted embed markup for a parsed id. Validates its own input so it can never be used as a
    /// generic markup factory; returns "" for anything that is not exactly one video id.
    public static string YoutubeEmbed(string? videoId)
    {
        var id = ParseYoutubeId(videoId);
        if (id is null) return "";
        return $"<div class=\"news-embed\" {YoutubeAttribute}=\"{id}\">" +
               $"<iframe src=\"https://www.youtube-nocookie.com/embed/{id}\" " +
               "title=\"YouTube video player\" loading=\"lazy\" frameborder=\"0\" allowfullscreen></iframe></div>";
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var k = i < 0 ? pair : pair[..i];
            if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var v = i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..]);
            return string.IsNullOrEmpty(v) ? null : v;
        }
        return null;
    }
}