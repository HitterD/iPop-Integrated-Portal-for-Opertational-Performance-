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
