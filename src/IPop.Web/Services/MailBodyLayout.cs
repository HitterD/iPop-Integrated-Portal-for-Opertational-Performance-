using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IPop.Web.Services;

/// Reading layout for rich / EPop mail (spec 2026-10-14 §5). A render-time transform of the sanitized body the API
/// serves; the stored body is never rewritten and "Original formatting" shows the input unchanged.
///   • inline font-family / line-height are dropped and font sizes clamped (13–17 px; headings by level),
///     so the reading column's 15 px / 1.6 applies. Colours, bold/italic and links stay.
///   • fixed widths wider than the column are dropped (images scale through CSS).
///   • quoted history (EPop "-----Original e/pop-----", Outlook From:/Sent:, gmail_quote, blockquote, thick left
///     borders) is moved into the same &lt;details class="mail-quote"&gt; expander iPop replies already use, so there
///     is one quote toggle. A body that already carries that expander is left as it is.
///   • empty vertical gaps (QA 3): runs of &lt;br&gt; keep at most one blank line, runs of empty paragraphs / divs /
///     tables keep the first, and empty spacers lose their fixed heights. Nothing with content goes: images, hr,
///     anchors, filled cells and coloured bars stay; &lt;pre&gt; is untouched.
/// The input is the HtmlSanitizer's serialization (well-formed, quoted attributes, escaped text). Anything
/// unexpected falls back to the input.
public static class MailBodyLayout
{
    public sealed record Result(string Html, bool Changed, bool QuoteCollapsed)
    {
        /// Worth offering the "Original formatting" switch: the reading view differs from what was sent.
        public bool OffersOriginal => Changed;
    }

    public const int ColumnPx = 760;
    private const double MinPx = 13, MaxPx = 17;

    public static Result Prepare(string? html)
    {
        if (string.IsNullOrEmpty(html)) return new Result("", false, false);
        if (html.IndexOf('<') < 0) return new Result(html, false, false);
        try
        {
            var root = Parse(html);
            // Quote detection first: it reads the thick left borders that normalisation drops.
            var quoted = !html.Contains("class=\"mail-quote", StringComparison.Ordinal) && CollapseQuote(root);
            var changed = NormaliseStyles(root);
            changed |= CollapseGaps(root);
            return changed || quoted ? new Result(Serialize(root), true, quoted) : new Result(html, false, false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or FormatException)
        {
            return new Result(html, false, false);
        }
    }

    // ───────────────────────── tree ─────────────────────────

    private abstract class Node { public El? Parent; }
    private sealed class Text(string raw) : Node { public string Raw = raw; }
    private sealed class Other(string raw) : Node { public string Raw = raw; }   // comments, doctype
    private sealed class El(string name) : Node
    {
        public readonly string Name = name;
        public readonly List<(string Name, string Value)> Attrs = [];
        public readonly List<Node> Kids = [];
        public string? Attr(string n) => Attrs.FirstOrDefault(a => a.Name == n).Value;
        public void SetAttr(string n, string? v)
        {
            var i = Attrs.FindIndex(a => a.Name == n);
            if (v is null) { if (i >= 0) Attrs.RemoveAt(i); return; }
            if (i >= 0) Attrs[i] = (n, v); else Attrs.Add((n, v));
        }
        public void Add(Node n) { n.Parent = this; Kids.Add(n); }
        public El Shallow() { var c = new El(Name); c.Attrs.AddRange(Attrs); return c; }
    }

    private static readonly HashSet<string> Void = ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"];
    private static readonly Regex Tag = new(@"<(/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:[^>""']|""[^""]*""|'[^']*')*?)(/?)>", RegexOptions.Compiled);
    private static readonly Regex AttrRx = new(@"([^\s=/>""']+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+)))?", RegexOptions.Compiled);

    private static El Parse(string html)
    {
        var root = new El("#root");
        var cur = root;
        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0) { cur.Add(new Text(html[i..])); break; }
            if (lt > i) cur.Add(new Text(html[i..lt]));
            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                end = end < 0 ? html.Length : end + 3;
                cur.Add(new Other(html[lt..end]));
                i = end;
                continue;
            }
            var m = Tag.Match(html, lt);
            if (!m.Success || m.Index != lt)
            {
                if (lt + 1 < html.Length && html[lt + 1] == '!')
                {
                    var gt = html.IndexOf('>', lt);
                    gt = gt < 0 ? html.Length : gt + 1;
                    cur.Add(new Other(html[lt..gt]));
                    i = gt;
                    continue;
                }
                cur.Add(new Text("&lt;"));
                i = lt + 1;
                continue;
            }
            var name = m.Groups[2].Value.ToLowerInvariant();
            if (m.Groups[1].Value == "/")
            {
                // Close the nearest open element of that name; a stray end tag is dropped.
                for (var e = cur; e is not null && e != root; e = e.Parent)
                    if (e.Name == name) { cur = e.Parent ?? root; break; }
            }
            else
            {
                var el = new El(name);
                foreach (Match a in AttrRx.Matches(m.Groups[3].Value))
                {
                    var v = a.Groups[2].Success ? a.Groups[2].Value
                          : a.Groups[3].Success ? a.Groups[3].Value.Replace("\"", "&quot;")
                          : a.Groups[4].Success ? a.Groups[4].Value : "";
                    el.Attrs.Add((a.Groups[1].Value.ToLowerInvariant(), v));
                }
                cur.Add(el);
                if (!Void.Contains(name) && m.Groups[4].Value != "/") cur = el;
            }
            i = m.Index + m.Length;
        }
        return root;
    }

    private static string Serialize(El root)
    {
        var sb = new StringBuilder();
        foreach (var k in root.Kids) Write(sb, k);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, Node n)
    {
        switch (n)
        {
            case Text t: sb.Append(t.Raw); break;
            case Other o: sb.Append(o.Raw); break;
            case El e:
                sb.Append('<').Append(e.Name);
                foreach (var (name, value) in e.Attrs) sb.Append(' ').Append(name).Append("=\"").Append(value).Append('"');
                sb.Append('>');
                if (Void.Contains(e.Name)) break;
                foreach (var k in e.Kids) Write(sb, k);
                sb.Append("</").Append(e.Name).Append('>');
                break;
        }
    }

    private static IEnumerable<Node> Walk(El e)
    {
        foreach (var k in e.Kids)
        {
            yield return k;
            if (k is El c) foreach (var d in Walk(c)) yield return d;
        }
    }

    // ───────────────────────── styles ─────────────────────────

    private static readonly HashSet<string> Dropped = ["font-family", "line-height", "border-left", "border-left-width", "border-left-style", "border-left-color"];

    private static bool NormaliseStyles(El root)
    {
        var changed = false;
        foreach (var el in Walk(root).OfType<El>())
        {
            if (el.Attr("style") is { } style)
            {
                var fixedStyle = Restyle(style, el.Name);
                if (fixedStyle != style) { el.SetAttr("style", fixedStyle.Length == 0 ? null : fixedStyle); changed = true; }
            }
            if (el.Name == "font")
            {
                if (el.Attr("face") is not null) { el.SetAttr("face", null); changed = true; }
                if (el.Attr("size") is { } sz && int.TryParse(sz.TrimStart('+'), out var n) && (n >= 5 || n <= 1)) { el.SetAttr("size", null); changed = true; }
            }
            if (el.Name is "table" or "td" or "th" or "div" && el.Attr("width") is { } w && Px(w) > ColumnPx)
            {
                el.SetAttr("width", null);
                changed = true;
            }
        }
        return changed;
    }

    private static string Restyle(string raw, string tag)
    {
        var decoded = raw.Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&amp;", "&");
        var parts = new List<string>();
        var touched = false;
        foreach (var d in decoded.Split(';'))
        {
            var colon = d.IndexOf(':');
            if (colon <= 0) { if (d.Trim().Length > 0) parts.Add(d.Trim()); continue; }
            var prop = d[..colon].Trim().ToLowerInvariant();
            var value = d[(colon + 1)..].Trim();
            if (Dropped.Contains(prop)) { touched = true; continue; }
            if (prop == "font-size")
            {
                touched = true;
                var px = FontPx(value);
                if (px is null) continue;                       // keywords / unknown units: the column's size
                var max = HeadingMax(tag) ?? MaxPx;
                if (px > max) parts.Add($"font-size:{max.ToString(CultureInfo.InvariantCulture)}px");
                else if (px < MinPx) parts.Add($"font-size:{MinPx.ToString(CultureInfo.InvariantCulture)}px");
                continue;                                       // in range: normalised to the column's size
            }
            if (prop is "width" or "min-width" or "height" && Px(value) > ColumnPx) { touched = true; continue; }
            parts.Add($"{prop}:{value}");
        }
        if (!touched) return raw;
        return string.Join(";", parts).Replace("&", "&amp;").Replace("\"", "&quot;");
    }

    private static double? HeadingMax(string tag) => tag switch { "h1" => 20, "h2" => 18, "h3" => 17, "h4" or "h5" or "h6" => 15, _ => null };

    private static readonly Regex Length = new(@"^\s*([0-9]*\.?[0-9]+)\s*(px|pt|em|rem|%)?\s*(!important)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static double? FontPx(string v)
    {
        var m = Length.Match(v);
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "pt" => n * 4 / 3,
            "em" or "rem" => n * 15,
            "%" => n * 15 / 100,
            _ => n,
        };
    }

    /// Absolute width in px (px or a bare number); null for %, auto and the rest.
    private static double? Px(string v)
    {
        var m = Length.Match(v);
        if (!m.Success) return null;
        var unit = m.Groups[2].Value.ToLowerInvariant();
        if (unit is not ("" or "px" or "pt")) return null;
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return unit == "pt" ? n * 4 / 3 : n;
    }

    // ───────────────────────── empty gaps ─────────────────────────

    /// Elements that may sit inside an empty spacer (wrappers and structure only — nothing that shows by itself).
    private static readonly HashSet<string> BlankOk =
    [
        "br", "span", "font", "b", "strong", "i", "em", "u", "s", "strike", "small", "big", "sub", "sup", "o:p",
        "p", "div", "center", "section", "table", "tbody", "thead", "tfoot", "tr", "td", "th", "colgroup", "col",
        "h1", "h2", "h3", "h4", "h5", "h6",
    ];
    /// Spacer blocks that collapse when several follow each other.
    private static readonly HashSet<string> GapBlocks = ["p", "div", "center", "section", "table", "h1", "h2", "h3", "h4", "h5", "h6"];
    private static readonly HashSet<string> Inline = ["span", "font", "b", "strong", "i", "em", "u", "s", "strike", "small", "big", "sub", "sup", "o:p"];
    /// Block-level elements: a line break right after one (or at the start of one) already starts a new line.
    private static readonly HashSet<string> BlockLevel =
    [
        "p", "div", "center", "section", "article", "header", "footer", "table", "ul", "ol", "li", "blockquote", "pre", "hr",
        "h1", "h2", "h3", "h4", "h5", "h6", "details", "summary", "td", "th", "body", "#root", "dl", "dd", "dt", "figure",
    ];
    private static readonly HashSet<string> Raw = ["pre", "textarea", "xmp", "listing", "plaintext"];
    private static readonly Regex Visible = new(@"(^|;)\s*(background|background-color|background-image|border|border-top|border-bottom|border-left|border-right|border-width|border-style)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeightDecl = new(@"^\s*(height|min-height)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool BlankText(string raw) => Decode(raw).Trim().Trim('​', '﻿', ' ').Trim().Length == 0;

    /// Shows nothing: only whitespace, breaks and plain wrappers; no images, rules, anchors, fields or coloured boxes.
    private static bool IsBlank(Node n) => n switch
    {
        Text t => BlankText(t.Raw),
        Other => true,
        El e => BlankOk.Contains(e.Name) && !Painted(e) && e.Kids.All(IsBlank),
        _ => false,
    };

    private static bool Painted(El e)
        => e.Attr("bgcolor") is not null || e.Attr("background") is not null
        || e.Attr("style") is { } st && Visible.IsMatch(st.Replace("&quot;", "\""))
        || e.Attr("id") is not null || e.Attr("name") is not null;

    /// A line-break-only node: &lt;br&gt;, whitespace, or an inline wrapper holding only those.
    private static bool BreakOnly(Node n) => n switch
    {
        Text t => BlankText(t.Raw),
        Other => true,
        El { Name: "br" } => true,
        El e => Inline.Contains(e.Name) && !Painted(e) && e.Kids.All(BreakOnly),
        _ => false,
    };

    private static int Breaks(Node n) => n switch
    {
        El { Name: "br" } => 1,
        El e => e.Kids.Sum(Breaks),
        _ => 0,
    };

    private static bool CollapseGaps(El root)
    {
        var changed = false;
        Visit(root);
        return changed;

        void Visit(El parent)
        {
            if (Raw.Contains(parent.Name)) return;
            foreach (var k in parent.Kids.ToList()) if (k is El c) Visit(c);
            if (parent.Name is "td" or "th" or "tr" && !Painted(parent) && parent.Kids.All(IsBlank)) changed |= DropHeights(parent);

            var kids = parent.Kids;
            var i = 0;
            while (i < kids.Count)
            {
                if (!(kids[i] is El { Name: "br" } || kids[i] is El ge && GapBlocks.Contains(ge.Name) && IsBlank(ge) || BreakOnly(kids[i]) && Breaks(kids[i]) > 0))
                { i++; continue; }
                // A gap run: breaks, whitespace, break-only wrappers and empty blocks, side by side.
                var start = i;
                var end = i;
                while (end < kids.Count && (BreakOnly(kids[end]) || kids[end] is El be && GapBlocks.Contains(be.Name) && IsBlank(be))) end++;
                while (end > start + 1 && kids[end - 1] is Text tail && BlankText(tail.Raw) && Breaks(tail) == 0) end--;
                var run = kids.GetRange(start, end - start);
                var firstBlock = run.OfType<El>().FirstOrDefault(e => GapBlocks.Contains(e.Name));
                List<Node>? keep = null;
                if (firstBlock is not null)
                {
                    // One empty block is one blank line; the rest of the run goes.
                    changed |= DropHeights(firstBlock);
                    if (run.Any(n => n != firstBlock && !(n is Text t && t.Raw.Trim().Length == 0)))
                        keep = [firstBlock];
                }
                else
                {
                    var prev = start > 0 ? PrevMeaningful(parent, kids[start]) : null;
                    var atLineStart = prev is null ? BlockLevel.Contains(parent.Name) : prev is El pe && BlockLevel.Contains(pe.Name);
                    var limit = atLineStart ? 1 : 2;
                    if (run.Sum(Breaks) > limit)
                    {
                        keep = [];
                        for (var b = 0; b < limit; b++) keep.Add(new El("br"));
                    }
                }
                if (keep is not null)
                {
                    kids.RemoveRange(start, run.Count);
                    foreach (var n in keep) n.Parent = parent;
                    kids.InsertRange(start, keep);
                    changed = true;
                    i = start + keep.Count;
                }
                else i = end;
            }
        }
    }

    /// An empty spacer keeps its place but not its fixed height (Outlook's 120 px empty divs / table rows).
    private static bool DropHeights(El e)
    {
        var changed = false;
        foreach (var el in new[] { e }.Concat(Walk(e).OfType<El>()))
        {
            if (el.Attr("height") is not null) { el.SetAttr("height", null); changed = true; }
            if (el.Attr("style") is { } st)
            {
                var parts = st.Split(';').Where(d => d.Trim().Length > 0).ToList();
                var kept = parts.Where(d => !HeightDecl.IsMatch(d)).ToList();
                if (kept.Count != parts.Count) { el.SetAttr("style", kept.Count == 0 ? null : string.Join(";", kept)); changed = true; }
            }
        }
        return changed;
    }

    // ───────────────────────── quoted history ─────────────────────────

    private static readonly Regex OriginalDivider = new(@"^\s*-{2,}\s*(Original (e/?pop|Message|Mail)|Forwarded message|Pesan Asli)\s*-{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FromLine = new(@"^\s*(From|Dari)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SentLine = new(@"(Sent|Date|Dikirim|Tanggal)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> Blocks = ["p", "div", "section", "article", "body", "center", "header", "footer"];
    private static readonly HashSet<string> NoSplit = ["table", "tbody", "thead", "tr", "td", "th", "li", "ul", "ol", "pre", "a"];

    private static bool CollapseQuote(El root)
    {
        var start = FindQuoteStart(root);
        if (start is null) return false;
        for (var a = start.Parent; a is not null && a != root; a = a.Parent)
            if (NoSplit.Contains(a.Name)) return false;
        if (!HasContentBefore(root, start)) return false;

        // Move the start node and everything after it (in document order) into the expander, cloning the
        // ancestors it was cut out of so the moved part keeps its structure.
        var parent = start.Parent!;
        var moved = TakeFrom(parent, parent.Kids.IndexOf(start));
        var cur = parent;
        while (cur != root)
        {
            var clone = cur.Shallow();
            foreach (var n in moved) clone.Add(n);
            var up = cur.Parent!;
            var idx = up.Kids.IndexOf(cur);
            var rest = TakeFrom(up, idx + 1);
            if (!HasAnyContent(cur)) up.Kids.Remove(cur);
            moved = [clone, .. rest];
            cur = up;
        }
        TrimTrailingBreaks(root);

        var details = new El("details");
        details.Attrs.Add(("class", "mail-quote mrd-quote"));
        var summary = new El("summary");
        summary.Attrs.Add(("class", "mail-quote-toggle"));
        summary.Attrs.Add(("title", "Show quoted text"));
        summary.Add(new Text("⋯"));
        var inner = new El("div");
        inner.Attrs.Add(("class", "mail-quote-inner"));
        foreach (var n in moved) inner.Add(n);
        details.Add(summary);
        details.Add(inner);
        root.Add(details);
        return true;
    }

    private static List<Node> TakeFrom(El parent, int index)
    {
        var taken = parent.Kids.Skip(index).ToList();
        parent.Kids.RemoveRange(index, parent.Kids.Count - index);
        return taken;
    }

    private static Node? FindQuoteStart(El root)
    {
        foreach (var n in Walk(root))
        {
            if (n is El e)
            {
                var cls = e.Attr("class") ?? "";
                var id = e.Attr("id") ?? "";
                if (cls.Contains("gmail_quote", StringComparison.Ordinal) || cls.Contains("yahoo_quoted", StringComparison.Ordinal)
                    || id is "divRplyFwdMsg" or "appendonsend" || e.Name == "blockquote"
                    || ThickLeftBorder(e.Attr("style")))
                    return e;
                continue;
            }
            if (n is not Text t) continue;
            var text = Decode(t.Raw);
            var divider = OriginalDivider.IsMatch(text);
            if (!divider && !(FromLine.IsMatch(text) && SentLine.IsMatch(TextFrom(t, 400)))) continue;
            var line = LineStart(t);
            if (line is null) continue;
            return line;
        }
        return null;
    }

    private static bool ThickLeftBorder(string? style)
    {
        if (style is null) return false;
        var m = Regex.Match(style, @"border-left\s*:\s*([0-9.]+)\s*(px|pt)", RegexOptions.IgnoreCase);
        return m.Success && double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) >= 3;
    }

    /// The node a quote line starts at: the text itself (or its inline wrapper) when it begins a line — first in
    /// its block or right after a &lt;br&gt; — promoted to the block when it is the block's first content.
    private static Node? LineStart(Text t)
    {
        Node x = t;
        while (true)
        {
            var p = x.Parent;
            if (p is null) return null;
            var prev = PrevMeaningful(p, x);
            if (prev is El { Name: "br" } || prev is El pe && Blocks.Contains(pe.Name)) return x;
            if (prev is not null) return null;              // mid-line: not a header line
            if (p.Name == "#root") return x;
            if (Blocks.Contains(p.Name) || p.Name == "blockquote") return p;
            x = p;                                           // inline wrapper (<b>From:</b>): climb
        }
    }

    private static Node? PrevMeaningful(El parent, Node child)
    {
        for (var i = parent.Kids.IndexOf(child) - 1; i >= 0; i--)
        {
            var k = parent.Kids[i];
            if (k is Text tx && Decode(tx.Raw).Trim().Length == 0) continue;
            if (k is Other) continue;
            return k;
        }
        return null;
    }

    /// Up to max characters of text from this text node onward (document order).
    private static string TextFrom(Text t, int max)
    {
        var sb = new StringBuilder();
        var root = t.Parent!;
        while (root.Parent is not null) root = root.Parent;
        var on = false;
        foreach (var n in Walk(root))
        {
            if (n == t) on = true;
            if (!on || n is not Text x) continue;
            sb.Append(Decode(x.Raw)).Append('\n');
            if (sb.Length >= max) break;
        }
        return sb.ToString();
    }

    private static bool HasContentBefore(El root, Node start)
    {
        foreach (var n in Walk(root))
        {
            if (n == start) return false;
            if (n is Text t && Decode(t.Raw).Trim().Length > 0) return true;
            if (n is El { Name: "img" or "table" or "hr" }) return true;
        }
        return false;
    }

    private static bool HasAnyContent(El e) => Walk(e).Any(n => n is El { Name: "img" or "table" or "hr" } || n is Text t && Decode(t.Raw).Trim().Length > 0);

    private static void TrimTrailingBreaks(El root)
    {
        // "…Bagastyo<br><br>" left over before the cut: drop the trailing breaks of the last block.
        var e = root;
        while (e.Kids.Count > 0 && e.Kids[^1] is El last && !Void.Contains(last.Name)) e = last;
        while (e.Kids.Count > 0 && (e.Kids[^1] is El { Name: "br" } || e.Kids[^1] is Text tx && Decode(tx.Raw).Trim().Length == 0))
            e.Kids.RemoveAt(e.Kids.Count - 1);
    }

    private static string Decode(string raw) => raw.Contains('&') ? System.Net.WebUtility.HtmlDecode(raw) : raw;
}
