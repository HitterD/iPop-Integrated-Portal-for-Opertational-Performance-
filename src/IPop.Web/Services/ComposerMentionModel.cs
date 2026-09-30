using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;

namespace IPop.Web.Services;

public readonly record struct CaretState(int Start, int End);
public readonly record struct TokenRange(int Start, int Length, MentionSegment Token) { public int End => Start + Length; }
public readonly record struct ActiveMentionToken(int AtIndex, string Query);

public readonly record struct ComposerKeyDecision(bool PreventDefault, string? Content, int? CaretStart, int? CaretEnd, ComposerKeyAction Action);
public enum ComposerKeyAction { None, MovePopupSelectionDown, MovePopupSelectionUp, CommitPopupSelection, ClosePopup, SendMessage, DeleteTokenRange, SkipTokenRange }

public static class ComposerMentionModel
{
    public const int MaxQueryLength = 40;

    public static ActiveMentionToken? DetectActive(string content, int caret)
    {
        if (string.IsNullOrEmpty(content) || caret <= 0) return null;
        int at = content.LastIndexOf('@', caret - 1);
        if (at < 0) return null;
        if (at > 0 && content[at - 1] != ' ' && content[at - 1] != '\n' && content[at - 1] != '\r') return null;
        var query = content.Substring(at + 1, caret - at - 1);
        if (query.Contains(' ') || query.Contains('\n') || query.Contains('\r')) return null;
        if (query.Length > MaxQueryLength) return null;
        return new ActiveMentionToken(at, query);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Plain-textarea mention model. The composer shows readable "@Full Name" text;
    // picked mentions live in a side list and become @[Name](u:GUID) tokens only
    // at send time. Editing/removing the "@Name" text simply drops the mention.
    // ──────────────────────────────────────────────────────────────────────────

    /// Caret-aware trigger detection that tolerates spaces inside the query so
    /// multi-word names ("@alphin san") keep filtering. The trigger must sit at the
    /// start or after whitespace (so e-mail addresses never open the popup).
    public static ActiveMentionToken? DetectActiveAt(string content, int caret, char trigger = '@')
    {
        if (string.IsNullOrEmpty(content) || caret <= 0 || caret > content.Length) return null;
        int at = content.LastIndexOf(trigger, caret - 1);
        if (at < 0) return null;
        if (at > 0 && !char.IsWhiteSpace(content[at - 1])) return null;
        var query = content.Substring(at + 1, caret - at - 1);
        if (query.Length > MaxQueryLength) return null;
        if (query.Contains('\n') || query.Contains('\r') || query.Contains('@')) return null;
        if (query.StartsWith(' ') || query.Contains("  ")) return null;
        if (query.StartsWith('[')) return null; // already a wire token
        return new ActiveMentionToken(at, query);
    }

    /// Replace the active "@query" with "@Name " and report where the caret lands.
    public static (string Content, int Caret) InsertDisplay(string content, ActiveMentionToken active, string name)
    {
        var insert = "@" + name + " ";
        var end = Math.Min(content.Length, active.AtIndex + 1 + active.Query.Length);
        var before = content[..active.AtIndex];
        var after = content[end..];
        if (after.StartsWith(' ')) after = after[1..]; // don't double the separator
        return (before + insert + after, before.Length + insert.Length);
    }

    /// Mention names are printed inside [...]; strip characters that would break the token.
    public static string SafeName(string name)
        => name.Replace("[", "").Replace("]", "").Replace("\r", " ").Replace("\n", " ").Trim();

    /// True when "@name" occurs at a mention boundary in the text.
    public static bool ContainsDisplay(string text, string name)
        => FindDisplay(text, name, 0) >= 0;

    private static int FindDisplay(string text, string name, int from)
    {
        var needle = "@" + name;
        var i = from;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            var end = i + needle.Length;
            var okBefore = i == 0 || char.IsWhiteSpace(text[i - 1]) || text[i - 1] is '(' or '"' or '\'';
            var okAfter = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (okBefore && okAfter) return i;
            i++;
        }
        return -1;
    }

    /// Drop picked mentions whose "@Name" is no longer in the text.
    public static List<MentionSegment> Prune(string text, IEnumerable<MentionSegment> picked)
        => picked.Where(p => p switch
        {
            MentionSegment.User u => ContainsDisplay(text, u.Name),
            MentionSegment.All => ContainsDisplay(text, MentionSyntax.AllLabel),
            _ => false,
        }).Distinct().ToList();

    /// Convert composer display text + picked mentions into the wire format the
    /// server parses. Existing tokens (project/task chips, pasted tokens) pass through.
    public static string ToWire(string text, IEnumerable<MentionSegment> picked)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var candidates = picked
            .Select(p => p switch
            {
                MentionSegment.User u => (Name: u.Name, Seg: (MentionSegment)u),
                MentionSegment.All a => (Name: MentionSyntax.AllLabel, Seg: (MentionSegment)a),
                _ => (Name: "", Seg: p),
            })
            .Where(c => c.Name.Length > 0)
            .GroupBy(c => c.Name, StringComparer.Ordinal).Select(g => g.First())
            .OrderByDescending(c => c.Name.Length) // "@Ann Lee" must win over "@Ann"
            .ToList();
        if (candidates.Count == 0) return text;

        var output = new List<MentionSegment>();
        foreach (var seg in MentionParser.Parse(text))
        {
            if (seg is not MentionSegment.Text t) { output.Add(seg); continue; }
            var s = t.Value;
            var buf = new System.Text.StringBuilder();
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '@')
                {
                    var hit = candidates.FirstOrDefault(c => FindDisplay(s, c.Name, i) == i);
                    if (hit.Seg is not null)
                    {
                        if (buf.Length > 0) { output.Add(new MentionSegment.Text(buf.ToString())); buf.Clear(); }
                        output.Add(hit.Seg);
                        i += 1 + hit.Name.Length;
                        continue;
                    }
                }
                buf.Append(s[i]);
                i++;
            }
            if (buf.Length > 0) output.Add(new MentionSegment.Text(buf.ToString()));
        }
        return MentionPrinter.Print(output);
    }

    /// Inverse of ToWire for editing and drafts: user/@all tokens become readable
    /// "@Name" text plus the picked list; project/task tokens stay as they are.
    public static (string Text, List<MentionSegment> Picked) FromWire(string? content)
    {
        if (string.IsNullOrEmpty(content)) return ("", []);
        var sb = new System.Text.StringBuilder();
        var picked = new List<MentionSegment>();
        foreach (var seg in MentionParser.Parse(content))
        {
            switch (seg)
            {
                case MentionSegment.User u:
                    sb.Append('@').Append(u.Name);
                    if (!picked.Contains(u)) picked.Add(u);
                    break;
                case MentionSegment.All a:
                    sb.Append('@').Append(MentionSyntax.AllLabel);
                    if (!picked.Any(p => p is MentionSegment.All)) picked.Add(a);
                    break;
                default:
                    sb.Append(MentionPrinter.Print([seg]));
                    break;
            }
        }
        return (sb.ToString(), picked);
    }

    public static IReadOnlyList<TokenRange> TokenRanges(string content)
    {
        var segs = MentionParser.Parse(content);
        var list = new List<TokenRange>();
        int pos = 0;
        foreach (var s in segs)
        {
            var raw = MentionPrinter.Print([s]);
            if (s is not MentionSegment.Text) list.Add(new TokenRange(pos, raw.Length, s));
            pos += raw.Length;
        }
        return list;
    }

    public static (string Content, int Caret)? Insert(string content, ActiveMentionToken active, MentionSegment token)
    {
        var tokenStr = MentionPrinter.PrintToken(token) + " ";
        var before = content[..active.AtIndex];
        var after = content[(active.AtIndex + 1 + active.Query.Length)..];
        var result = before + tokenStr + after;
        if (result.Length > 4000) return null;
        return (result, before.Length + tokenStr.Length);
    }

    public static IReadOnlyList<MemberDto> Filter(IReadOnlyList<MemberDto> candidates, string query, int take = 10)
    {
        var filtered = string.IsNullOrEmpty(query)
            ? candidates.ToList()
            : candidates.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Nik.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return filtered.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Nik, StringComparer.OrdinalIgnoreCase).Take(take).ToList();
    }

    // Requirement 2.x / 3.x: Composer key decisions (popup navigation, atomic token deletion, cursor skip)
    public static ComposerKeyDecision Decide(string content, CaretState caret, string key, bool shift, bool popupOpen)
    {
        var hasSelection = caret.End != caret.Start;
        var ranges = TokenRanges(content);

        // When popup is open: Enter/Tab/Escape navigate popup
        if (popupOpen)
        {
            if (key is "Enter" or "Tab")
                return new ComposerKeyDecision(true, null, null, null, ComposerKeyAction.CommitPopupSelection);
            if (key == "Escape")
                return new ComposerKeyDecision(true, null, null, null, ComposerKeyAction.ClosePopup);
            if (key == "ArrowDown")
                return new ComposerKeyDecision(true, null, null, null, ComposerKeyAction.MovePopupSelectionDown);
            if (key == "ArrowUp")
                return new ComposerKeyDecision(true, null, null, null, ComposerKeyAction.MovePopupSelectionUp);
        }
        else
        {
            if (key == "Enter" && !shift)
                return new ComposerKeyDecision(false, null, null, null, ComposerKeyAction.SendMessage);
        }

        if (!hasSelection)
        {
            if (key == "Backspace")
            {
                var hit = ranges.FirstOrDefault(r => r.End == caret.Start);
                if (hit.Token != null)
                {
                    var next = content[..hit.Start] + content[hit.End..];
                    return new ComposerKeyDecision(true, next, hit.Start, hit.Start, ComposerKeyAction.DeleteTokenRange);
                }
            }
            if (key == "Delete")
            {
                var hit = ranges.FirstOrDefault(r => r.Start == caret.Start);
                if (hit.Token != null)
                {
                    var next = content[..hit.Start] + content[hit.End..];
                    return new ComposerKeyDecision(true, next, hit.Start, hit.Start, ComposerKeyAction.DeleteTokenRange);
                }
            }
            if (key == "ArrowLeft")
            {
                var inside = ranges.FirstOrDefault(r => caret.Start > r.Start && caret.Start < r.End);
                if (inside.Token != null)
                    return new ComposerKeyDecision(true, null, inside.Start, inside.Start, ComposerKeyAction.SkipTokenRange);
                var atStart = ranges.FirstOrDefault(r => r.Start == caret.Start);
                if (atStart.Token != null)
                    return new ComposerKeyDecision(false, null, null, null, ComposerKeyAction.None);
                // If caret is right after a token, jump to its start in one keypress
                var rightAfter = ranges.FirstOrDefault(r => r.End == caret.Start);
                if (rightAfter.Token != null)
                    return new ComposerKeyDecision(true, null, rightAfter.Start, rightAfter.Start, ComposerKeyAction.SkipTokenRange);
            }
            if (key == "ArrowRight")
            {
                var inside = ranges.FirstOrDefault(r => caret.Start > r.Start && caret.Start < r.End);
                if (inside.Token != null)
                    return new ComposerKeyDecision(true, null, inside.End, inside.End, ComposerKeyAction.SkipTokenRange);
                var rightAtEnd = ranges.FirstOrDefault(r => r.End == caret.Start);
                // already at end — no skip
                var atStart = ranges.FirstOrDefault(r => r.Start == caret.Start);
                if (atStart.Token != null)
                    return new ComposerKeyDecision(true, null, atStart.End, atStart.End, ComposerKeyAction.SkipTokenRange);
            }
        }

        return new ComposerKeyDecision(false, null, null, null, ComposerKeyAction.None);
    }
}
