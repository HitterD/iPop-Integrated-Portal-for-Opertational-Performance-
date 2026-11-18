using IPop.Application.Notifications;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// "@Full Name" in a task comment notifies that project member (in-app, once per comment).
/// Only members of the task's project can be mentioned; the author never notifies themself.
/// The composer inserts the member's full name, so matching is on the whole name, case-insensitive,
/// and must not continue into another letter or digit ("@Dewi" does not match "@Dewianti").
public static class ProjectCommentMentions
{
    public static async Task<int> NotifyAsync(IPopDbContext db, INotificationGatewayService? gateway, Guid taskId, Guid authorId,
        string content, CancellationToken ct)
    {
        if (gateway is null || string.IsNullOrEmpty(content) || !content.Contains('@')) return 0;
        var task = await db.ProjectTasks.AsNoTracking().Where(t => t.Id == taskId)
            .Select(t => new { t.ProjectId, t.Title, ProjectName = t.Project.Name }).FirstOrDefaultAsync(ct);
        if (task is null) return 0;
        var members = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == task.ProjectId && m.UserId != authorId)
            .Select(m => new { m.UserId, m.User.Name }).ToListAsync(ct);
        var author = await db.Users.AsNoTracking().Where(u => u.Id == authorId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "Someone";
        var sent = 0;
        // Longest names first, and a matched mention is blanked, so "@Dewi Sartika" is not also read as "@Dewi".
        var rest = content;
        foreach (var m in members.Where(m => !string.IsNullOrWhiteSpace(m.Name)).OrderByDescending(m => m.Name.Length))
        {
            if (!Consume(ref rest, m.Name)) continue;
            var snippet = content.Length > 140 ? content[..140] + "…" : content;
            try
            {
                await gateway.PublishInternalAsync("projects", "project.task_mention", $"{author} mentioned you in {task.Title}",
                    $"\"{snippet}\" · {task.ProjectName}", new NotificationTarget(UserId: m.UserId), "normal",
                    $"/projects/{task.ProjectId}?task={taskId}", ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* advisory: the comment is saved */ }
        }
        return sent;
    }

    /// True when "@name" occurs as a whole mention; every such occurrence is blanked out of text.
    internal static bool Consume(ref string text, string name)
    {
        var token = "@" + name.Trim();
        var found = false;
        for (var i = text.IndexOf(token, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(token, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + token.Length;
            if (end < text.Length && char.IsLetterOrDigit(text[end])) continue;
            text = text[..i] + new string(' ', token.Length) + text[end..];
            found = true;
        }
        return found;
    }
}
