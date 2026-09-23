namespace IPop.Application.Chat.Mentions;

public static class MentionPlainText
{
    public static string Project(string content) => Project(content, int.MaxValue);

    public static string Project(string content, int maxLength)
    {
        var segs = MentionParser.Parse(content);
        var sb = new System.Text.StringBuilder();
        foreach (var s in segs)
        {
            string chunk = s switch
            {
                MentionSegment.Text t => t.Value,
                MentionSegment.User u => "@" + u.Name,
                MentionSegment.All => "@all",
                MentionSegment.Project p => "@" + p.Name,
                MentionSegment.Task t => "@" + t.Title,
                _ => ""
            };
            sb.Append(chunk);
        }
        var result = sb.ToString();
        return result.Length <= maxLength ? result : result[..maxLength];
    }
}
