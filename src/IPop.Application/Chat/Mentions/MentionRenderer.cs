using System.Text.RegularExpressions;

namespace IPop.Application.Chat.Mentions;

public static class MentionRenderer
{
    // Legacy plain-text "@word" (pre-token messages). The look-behind skips e-mail addresses.
    private static readonly Regex LegacyRx = new(@"(?<![\w.@])@\w+", RegexOptions.Compiled);

    public static string ToHtml(string content, IMentionDirectory directory, bool isGroupRoom)
        => ToHtml(content, directory, isGroupRoom, null, null);

    /// <param name="currentUserNik">Viewer's NIK: their own mentions get the "me" class.</param>
    /// <param name="legacyMembers">Room members (NIK, full name). Legacy "@Full Name" text that
    /// matches one exactly is highlighted as one clickable unit instead of just the first word.</param>
    public static string ToHtml(string content, IMentionDirectory directory, bool isGroupRoom,
        string? currentUserNik, IReadOnlyList<(string Nik, string Name)>? legacyMembers)
    {
        var legacyRx = BuildLegacyRx(legacyMembers, out var byName);
        var segs = MentionParser.Parse(content);
        var sb = new System.Text.StringBuilder();
        foreach (var seg in segs)
        {
            switch (seg)
            {
                case MentionSegment.Text t:
                    sb.Append(RenderLegacy(Escape(t.Value), legacyRx, byName, currentUserNik));
                    break;
                case MentionSegment.User u:
                    var escName = Escape(u.Name);
                    // Resolved in groups and DMs alike; unknown/left users render muted.
                    if (!directory.TryResolve(u.UserId, out var nik, out _, out var active) || !active)
                        sb.Append($"<span class=\"mention inactive\">@{escName}</span>");
                    else
                        sb.Append(UserSpan(nik, "@" + escName, IsMe(nik, currentUserNik), legacy: false));
                    break;
                case MentionSegment.All:
                    sb.Append("<span class=\"mention mention-all-tag\" title=\"Everyone in this group\">@all</span>");
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

    private static bool IsMe(string nik, string? me)
        => !string.IsNullOrEmpty(me) && string.Equals(nik, me, StringComparison.OrdinalIgnoreCase);

    private static string UserSpan(string nik, string escapedLabel, bool me, bool legacy)
        => $"<span class=\"mention user-mention{(legacy ? " legacy" : "")}{(me ? " me" : "")}\" data-nik=\"{Escape(nik)}\" role=\"button\" tabindex=\"0\" title=\"View profile\">{escapedLabel}</span>";

    private static Regex? BuildLegacyRx(IReadOnlyList<(string Nik, string Name)>? members, out Dictionary<string, string> byName)
    {
        byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (members is null || members.Count == 0) return null;
        foreach (var (nik, name) in members)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(nik)) continue;
            byName.TryAdd(Escape(name.Trim()), nik);
        }
        if (byName.Count == 0) return null;
        // Longest first so "@Ann Lee" beats "@Ann".
        var alternation = string.Join("|", byName.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        return new Regex($@"(?<![\w.@])@(?:(?<name>{alternation})(?![\w])|\w+)", RegexOptions.IgnoreCase);
    }

    private static string RenderLegacy(string escaped, Regex? namesRx, Dictionary<string, string> byName, string? me)
    {
        if (namesRx is null)
            return LegacyRx.Replace(escaped, m => $"<span class=\"mention legacy\">{m.Value}</span>");
        return namesRx.Replace(escaped, m =>
        {
            var g = m.Groups["name"];
            if (g.Success && byName.TryGetValue(g.Value, out var nik))
                return UserSpan(nik, m.Value, IsMe(nik, me), legacy: true);
            return $"<span class=\"mention legacy\">{m.Value}</span>";
        });
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#39;");
}
