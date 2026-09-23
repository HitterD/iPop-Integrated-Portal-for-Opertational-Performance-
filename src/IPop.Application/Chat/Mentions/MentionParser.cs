namespace IPop.Application.Chat.Mentions;

public static class MentionParser
{
    public static IReadOnlyList<MentionSegment> Parse(string content)
    {
        if (string.IsNullOrEmpty(content)) return [new MentionSegment.Text(content ?? "")];
        var result = new List<MentionSegment>();
        var buf = new System.Text.StringBuilder();
        int i = 0;
        while (i < content.Length)
        {
            if (TryParseToken(content, i, out var seg, out var len))
            {
                if (buf.Length > 0) { result.Add(new MentionSegment.Text(buf.ToString())); buf.Clear(); }
                result.Add(seg!);
                i += len;
            }
            else
            {
                buf.Append(content[i]);
                i++;
            }
        }
        if (buf.Length > 0) result.Add(new MentionSegment.Text(buf.ToString()));
        else if (result.Count == 0) result.Add(new MentionSegment.Text(""));
        return result;
    }

    private static bool TryParseToken(string s, int pos, out MentionSegment? seg, out int len)
    {
        seg = null; len = 0;
        if (pos + 2 >= s.Length || s[pos] != '@' || s[pos + 1] != '[') return false;
        var close = s.IndexOf(']', pos + 2);
        if (close < 0) return false;
        var name = s.Substring(pos + 2, close - pos - 2);
        if (name.Contains('[') || name.Contains(']') || name.Contains('\r') || name.Contains('\n')) return false;
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MentionSyntax.MaxNameLength) return false;
        if (close + 1 >= s.Length || s[close + 1] != '(') return false;
        var closeParen = s.IndexOf(')', close + 2);
        if (closeParen < 0) return false;
        var inner = s.Substring(close + 2, closeParen - close - 2);
        if (inner == MentionSyntax.AllLabel)
        {
            if (trimmed != MentionSyntax.AllLabel) return false;
            seg = new MentionSegment.All();
            len = closeParen - pos + 1;
            return true;
        }
        if (inner.StartsWith("u:", StringComparison.Ordinal))
        {
            var guidStr = inner.Substring(2);
            if (!Guid.TryParseExact(guidStr, "D", out var guid)) return false;
            seg = new MentionSegment.User(guid, trimmed);
            len = closeParen - pos + 1;
            return true;
        }
        if (inner.StartsWith("p:", StringComparison.Ordinal))
        {
            var guidStr = inner.Substring(2);
            if (!Guid.TryParseExact(guidStr, "D", out var guid)) return false;
            seg = new MentionSegment.Project(guid, trimmed);
            len = closeParen - pos + 1;
            return true;
        }
        if (inner.StartsWith("t:", StringComparison.Ordinal))
        {
            var guidStr = inner.Substring(2);
            if (!Guid.TryParseExact(guidStr, "D", out var guid)) return false;
            seg = new MentionSegment.Task(guid, trimmed);
            len = closeParen - pos + 1;
            return true;
        }
        return false;
    }

    public static IReadOnlySet<Guid> UserIds(IReadOnlyList<MentionSegment> segments)
        => segments.OfType<MentionSegment.User>().Select(u => u.UserId).ToHashSet();

    public static bool HasAll(IReadOnlyList<MentionSegment> segments)
        => segments.Any(s => s is MentionSegment.All);

    public static int TokenCount(IReadOnlyList<MentionSegment> segments)
        => segments.Count(s => s is not MentionSegment.Text);
}
