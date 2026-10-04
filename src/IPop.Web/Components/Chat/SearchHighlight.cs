using System.Net;
using System.Text;
using Microsoft.AspNetCore.Components;

namespace IPop.Web.Components.Chat;

/// Micro-interaction #38: wraps every case-insensitive occurrence of the query in
/// &lt;mark class="mi-hl"&gt;. Text is HTML-encoded first, so the result is safe as markup.
public static class SearchHighlight
{
    public static MarkupString Mark(string? text, string? query)
    {
        text ??= "";
        var q = query?.Trim() ?? "";
        if (q.Length < 2) return new MarkupString(WebUtility.HtmlEncode(text));
        var sb = new StringBuilder(text.Length + 32);
        var at = 0;
        while (at < text.Length)
        {
            var hit = text.IndexOf(q, at, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;
            sb.Append(WebUtility.HtmlEncode(text[at..hit]))
              .Append("<mark class=\"mi-hl\">").Append(WebUtility.HtmlEncode(text.Substring(hit, q.Length))).Append("</mark>");
            at = hit + q.Length;
        }
        sb.Append(WebUtility.HtmlEncode(text[at..]));
        return new MarkupString(sb.ToString());
    }
}
