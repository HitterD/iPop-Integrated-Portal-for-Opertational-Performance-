using System.Text.RegularExpressions;

namespace IPop.Application.Links;

public enum LinkKind { Web, Email, Unc }

/// A detected link inside a run of plain text. <see cref="Start"/>/<see cref="Length"/> index the
/// original string; <see cref="Display"/> is that exact slice. <see cref="Href"/> is the target:
/// an absolute http(s) URL for Web (www. gains https://), "mailto:addr" for Email, the raw path for Unc.
public sealed record LinkSpan(int Start, int Length, LinkKind Kind, string Href, string Display);

/// Finds web URLs, bare www. hosts, email addresses and Windows UNC paths in plain text.
/// Shared by mail linkification (server) and chat rendering (WASM) — keep it dependency-free.
public static partial class LinkDetector
{
    // http(s)://… or www.… — stops at whitespace, angle brackets and quotes. The lookbehind keeps
    // "foo.www.x" / "user@www.x" from matching mid-token.
    [GeneratedRegex(@"(?<![\w@./\\-])(?:https?://|www\.)[^\s<>""'\u00A0\u3000]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebRx();

    [GeneratedRegex(@"(?<![\w.%+\-@/])[A-Za-z0-9](?:[A-Za-z0-9._%+\-]{0,62}[A-Za-z0-9_%+\-])?@[A-Za-z0-9](?:[A-Za-z0-9\-]{0,62})(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,62}))*\.[A-Za-z]{2,24}(?![\w@])", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRx();

    // \\host\share[\rest…] — host is a name or IPv4; the path may contain spaces and runs to the end
    // of the line (or a character Windows forbids in paths), stopping early at a double space or
    // before a following web link. Trailing punctuation is trimmed after.
    [GeneratedRegex(@"(?<![\w\\])\\\\[A-Za-z0-9][A-Za-z0-9.\-_$]*\\[^\\\r\n<>""|*?\t/:](?:(?!\s{2}|\s+(?:https?://|www\.))[^\r\n<>""|*?\t])*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UncRx();

    private const string TrailingPunct = ".,;:!?'\"…";

    public static IReadOnlyList<LinkSpan> Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var spans = new List<LinkSpan>();

        foreach (Match m in UncRx().Matches(text))
        {
            var len = TrimEnd(text, m.Index, m.Length, alsoWhitespace: true);
            // Need at least "\\h\s".
            if (len < 5) continue;
            var raw = text.Substring(m.Index, len);
            spans.Add(new LinkSpan(m.Index, len, LinkKind.Unc, raw, raw));
        }

        foreach (Match m in WebRx().Matches(text))
        {
            if (Overlaps(spans, m.Index, m.Length)) continue;
            var len = TrimEnd(text, m.Index, m.Length, alsoWhitespace: false);
            var raw = text.Substring(m.Index, len);
            var isWww = raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
            var prefixLen = isWww ? 4 : raw.IndexOf("://", StringComparison.Ordinal) + 3;
            // Require something host-like after the scheme / www.
            if (raw.Length <= prefixLen) continue;
            var host = raw[prefixLen..];
            var hostEnd = host.IndexOfAny(['/', '?', '#']);
            if (hostEnd >= 0) host = host[..hostEnd];
            if (host.Length == 0 || host.StartsWith('.') || (isWww && !host.Contains('.'))) continue;
            var href = isWww ? "https://" + raw : raw;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) continue;
            spans.Add(new LinkSpan(m.Index, len, LinkKind.Web, href, raw));
        }

        foreach (Match m in EmailRx().Matches(text))
        {
            if (Overlaps(spans, m.Index, m.Length)) continue;
            spans.Add(new LinkSpan(m.Index, m.Length, LinkKind.Email, "mailto:" + m.Value, m.Value));
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    private static bool Overlaps(List<LinkSpan> spans, int start, int length)
    {
        foreach (var s in spans)
            if (start < s.Start + s.Length && s.Start < start + length) return true;
        return false;
    }

    /// Drops trailing sentence punctuation and unbalanced closing brackets: "(see http://x/a_(b))."
    /// keeps "http://x/a_(b)".
    private static int TrimEnd(string text, int start, int length, bool alsoWhitespace)
    {
        var len = length;
        while (len > 0)
        {
            var c = text[start + len - 1];
            if (TrailingPunct.IndexOf(c) >= 0 || (alsoWhitespace && char.IsWhiteSpace(c))) { len--; continue; }
            var open = c switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
            if (open != '\0')
            {
                var span = text.AsSpan(start, len);
                int opens = 0, closes = 0;
                foreach (var ch in span) { if (ch == open) opens++; else if (ch == c) closes++; }
                if (closes > opens) { len--; continue; }
            }
            break;
        }
        return len;
    }
}
