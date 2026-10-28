using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using IPop.Application.Links;

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
        s.AllowedAttributes.Add("data-unc");     // linkified \\host\share paths (client opens them)
        s.AllowedAttributes.Add(MailInlineImages.Attr); // pasted image stored on the NAS (client loads it with the token)
        s.AllowedAttributes.Add("target");
        s.AllowedAttributes.Add("rel");

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

        // Images moved to the attachment store keep their id only on <img>, and only as a GUID.
        // Their src is a same-origin relative /api/attachments/{id}, which the default sanitizer keeps.
        s.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement el || el.GetAttribute(MailInlineImages.Attr) is not { } att) return;
            if (!el.TagName.Equals("IMG", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(att, out Guid _))
                el.RemoveAttribute(MailInlineImages.Attr);
        };

        // Office-grade formatting from the editor and from Word paste.
        foreach (var p in new[] { "line-height", "margin-left", "margin-top", "margin-bottom", "padding-left", "vertical-align", "text-decoration",
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

    /// What readers get: sanitized, plain-text links made clickable, sanitized again so the
    /// linkify output is held to the same allow-list as everything else.
    public static string ForDisplay(string? html) =>
        string.IsNullOrEmpty(html) ? "" : Sanitizer.Sanitize(Linkify(Sanitizer.Sanitize(html)));

    /// For server-side rendering (PDF export): the renderer runs on the server and would fetch any
    /// URL in the body, so an `img src="http://10.0.0.1/admin"` was a server-side request forgery.
    /// Only self-contained data: URIs survive; every other resource reference is removed. Links
    /// (`a href`) stay, they are just text in a PDF and are never fetched.
    public static string ForOfflineRender(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var doc = new HtmlParser().ParseDocument("<!doctype html><html><body>" + Sanitize(html) + "</body></html>");
        if (doc.Body is null) return "";
        foreach (var el in doc.Body.QuerySelectorAll("*").ToList())
        {
            foreach (var name in new[] { "src", "srcset", "background", "poster", "data", "lowsrc", "dynsrc" })
            {
                var v = el.GetAttribute(name);
                if (v is not null && !v.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    el.RemoveAttribute(name);
            }
            if (el.GetAttribute("style") is { } style && style.Contains("url(", StringComparison.OrdinalIgnoreCase))
                el.SetAttribute("style", CssUrl.Replace(style, "none"));
            // An image with nothing left to show: drop it instead of a broken-image box.
            if (el.TagName.Equals("IMG", StringComparison.OrdinalIgnoreCase) && !el.HasAttribute("src")) el.Remove();
        }
        return doc.Body.InnerHtml;
    }

    private static readonly System.Text.RegularExpressions.Regex CssUrl = new(
        @"url\s*\([^)]*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly HashSet<string> NoLinkifyTags = new(StringComparer.OrdinalIgnoreCase)
        { "A", "CODE", "PRE", "STYLE", "SCRIPT", "TEXTAREA", "BUTTON", "SELECT", "OPTION", "TITLE" };

    /// Wraps URLs, www. hosts, email addresses and \\host\share paths found in TEXT nodes in
    /// anchors (see <see cref="LinkDetector"/>). Text already inside an anchor, code/pre, style/script
    /// or a mention/project chip is left alone, so running it twice changes nothing. External links
    /// open in a new tab without referrer; UNC paths become <c>&lt;a class="unc-link" href="#"
    /// data-unc="…"&gt;</c> for the client to handle.
    public static string Linkify(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var doc = new HtmlParser().ParseDocument("<!doctype html><html><body>" + html + "</body></html>");
        var body = doc.Body;
        if (body is null) return html;

        var targets = new List<IText>();
        Collect(body, targets);
        if (targets.Count == 0) return html;

        foreach (var text in targets)
        {
            var value = text.Data;
            var spans = LinkDetector.Find(value);
            if (spans.Count == 0) continue;
            var parent = text.Parent!;
            var pos = 0;
            foreach (var span in spans)
            {
                if (span.Start > pos) parent.InsertBefore(doc.CreateTextNode(value[pos..span.Start]), text);
                var a = doc.CreateElement("a");
                switch (span.Kind)
                {
                    case LinkKind.Web:
                        a.SetAttribute("href", span.Href);
                        a.SetAttribute("target", "_blank");
                        a.SetAttribute("rel", "noopener noreferrer");
                        break;
                    case LinkKind.Email:
                        a.SetAttribute("href", span.Href);
                        break;
                    case LinkKind.Unc:
                        a.SetAttribute("class", "unc-link");
                        a.SetAttribute("href", "#");
                        a.SetAttribute("data-unc", span.Href);
                        break;
                }
                a.TextContent = span.Display;
                parent.InsertBefore(a, text);
                pos = span.Start + span.Length;
            }
            if (pos < value.Length) parent.InsertBefore(doc.CreateTextNode(value[pos..]), text);
            parent.RemoveChild(text);
        }
        return body.InnerHtml;
    }

    private static void Collect(INode node, List<IText> into)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText t)
            {
                if (t.Data.Length > 3) into.Add(t);
            }
            else if (child is IElement e)
            {
                if (NoLinkifyTags.Contains(e.TagName) || e.HasAttribute("data-nik") || e.HasAttribute("data-project")) continue;
                Collect(e, into);
            }
        }
    }

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var noTags = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return System.Text.RegularExpressions.Regex.Replace(decoded, @"\s+", " ").Trim();
    }
}
