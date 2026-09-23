namespace IPop.Application.Chat.Mentions;

public static class MentionPrinter
{
    public static string Print(IReadOnlyList<MentionSegment> segments)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in segments)
        {
            switch (s)
            {
                case MentionSegment.Text t: sb.Append(t.Value); break;
                case MentionSegment.User u: sb.Append($"@[{u.Name}](u:{u.UserId:D})"); break;
                case MentionSegment.All: sb.Append($"@[{MentionSyntax.AllLabel}]({MentionSyntax.AllLabel})"); break;
                case MentionSegment.Project p: sb.Append($"@[{p.Name}](p:{p.ProjectId:D})"); break;
                case MentionSegment.Task t: sb.Append($"@[{t.Title}](t:{t.TaskId:D})"); break;
            }
        }
        return sb.ToString();
    }

    public static string PrintToken(MentionSegment token) => Print([token]);

    public static bool IsPrintable(IReadOnlyList<MentionSegment> segments)
    {
        foreach (var s in segments)
        {
            if (s is MentionSegment.Text t && ContainsTokenSyntax(t.Value)) return false;
            if (s is MentionSegment.User u && (string.IsNullOrWhiteSpace(u.Name) || u.Name.Length > MentionSyntax.MaxNameLength || u.Name.Contains('[') || u.Name.Contains(']') || u.Name.Contains('\r') || u.Name.Contains('\n'))) return false;
        }
        return segments.Count(s => s is not MentionSegment.Text) <= MentionSyntax.MaxTokensPerMessage;
    }

    private static bool ContainsTokenSyntax(string v) => v.Contains("@[");
}
