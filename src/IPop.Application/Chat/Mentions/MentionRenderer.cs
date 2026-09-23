using System.Text.RegularExpressions;

namespace IPop.Application.Chat.Mentions;

public static class MentionRenderer
{
    private static readonly Regex LegacyRx = new(@"@\w+", RegexOptions.Compiled);

    public static string ToHtml(string content, IMentionDirectory directory, bool isGroupRoom)
    {
        var segs = MentionParser.Parse(content);
        var sb = new System.Text.StringBuilder();
        foreach (var seg in segs)
        {
            switch (seg)
            {
                case MentionSegment.Text t:
                    sb.Append(RenderLegacy(Escape(t.Value)));
                    break;
                case MentionSegment.User u:
                    var escName = Escape(u.Name);
                    if (!isGroupRoom || !directory.TryResolve(u.UserId, out var nik, out _, out var active) || !active)
                        sb.Append($"<span class=\"mention inactive\">@{escName}</span>");
                    else
                        sb.Append($"<span class=\"mention\" data-nik=\"{Escape(nik)}\" style=\"cursor:pointer\">@{escName}</span>");
                    break;
                case MentionSegment.All:
                    sb.Append("<span class=\"mention\">@all</span>");
                    break;
                case MentionSegment.Project p:
                    var escProj = Escape(p.Name);
                    sb.Append($"<span class=\"mention project-chip\" data-project-id=\"{p.ProjectId:D}\" style=\"cursor:pointer\" title=\"Jump to project\">📁 {escProj}</span>");
                    break;
                case MentionSegment.Task t:
                    var escTask = Escape(t.Title);
                    sb.Append($"<span class=\"mention task-chip\" data-task-id=\"{t.TaskId:D}\" style=\"cursor:pointer\" title=\"Jump to task\">✅ {escTask}</span>");
                    break;
            }
        }
        return sb.ToString();
    }

    private static string RenderLegacy(string escaped)
    {
        return LegacyRx.Replace(escaped, m => $"<span class=\"mention legacy\">" + $"{m.Value}</span>");
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#39;");
}
