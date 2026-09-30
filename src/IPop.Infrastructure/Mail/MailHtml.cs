using Ganss.Xss;

namespace IPop.Infrastructure.Mail;

/// Mail bodies come from the rich-text composer and are rendered as raw markup by the
/// client, so every stored and served body goes through this allow-list first.
public static class MailHtml
{
    private static readonly HtmlSanitizer Sanitizer = Create();

    private static HtmlSanitizer Create()
    {
        var s = new HtmlSanitizer();
        s.AllowedSchemes.Add("mailto");
        s.AllowedAttributes.Add("class");
        s.AllowedAttributes.Add("data-nik");     // @mention chips
        s.AllowedAttributes.Add("data-project"); // linked-project chips

        // Pasted/dropped images are stored inline as data: URIs. Only raster images on <img>,
        // capped per image — never data: on links, CSS url() or other MIME types (svg can script).
        s.AllowedSchemes.Add("data");
        s.FilterUrl += (_, e) =>
        {
            if (!e.OriginalUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            var ok = e.Tag.TagName.Equals("IMG", StringComparison.OrdinalIgnoreCase)
                && e.Tag.GetAttribute("src") == e.OriginalUrl
                && DataImage.IsMatch(e.OriginalUrl)
                && e.OriginalUrl.Length <= MaxDataImageChars;
            if (!ok) e.SanitizedUrl = null;
        };

        // Office-grade formatting from the editor and from Word paste.
        foreach (var p in new[] { "line-height", "margin-left", "padding-left", "vertical-align", "text-decoration",
                                  "border-collapse", "width", "height", "font-family", "font-size", "color",
                                  "background-color", "text-align" })
            s.AllowedCssProperties.Add(p);
        foreach (var t in new[] { "sub", "sup", "hr", "mark" }) s.AllowedTags.Add(t);
        foreach (var a in new[] { "colspan", "rowspan" }) s.AllowedAttributes.Add(a);
        return s;
    }

    // 2 MB of binary is ~2.8M base64 chars; the prefix is a few dozen more.
    public const int MaxDataImageChars = 2 * 1024 * 1024 / 3 * 4 + 64;

    private static readonly System.Text.RegularExpressions.Regex DataImage = new(
        @"^data:image/(png|jpeg|gif|webp);base64,[A-Za-z0-9+/=\s]+$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string Sanitize(string? html) =>
        string.IsNullOrEmpty(html) ? "" : Sanitizer.Sanitize(html);

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var noTags = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return System.Text.RegularExpressions.Regex.Replace(decoded, @"\s+", " ").Trim();
    }
}
