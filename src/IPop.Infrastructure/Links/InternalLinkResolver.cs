using System.Text.RegularExpressions;
using IPop.Application.Links;
using IPop.Application.News;
using IPop.Application.Notes;
using IPop.Application.Projects;
using IPop.Application.Rbac;

namespace IPop.Infrastructure.Links;

/// Turns an iPop URL into native card data — only what the viewer could open themselves.
/// Uses the same services (and therefore the same visibility rules) as the news and project
/// endpoints; anything unknown or not visible degrades to a bare "page" with the path only.
public sealed partial class InternalLinkResolver(
    INewsService news, IProjectService projects, IPermissionEvaluator perms, INoteService notes)
{
    [GeneratedRegex(@"^/news/(?<id>[0-9a-fA-F\-]{32,36})/?$")]
    private static partial Regex NewsPath();

    [GeneratedRegex(@"^/projects/(?<id>[0-9a-fA-F\-]{32,36})/?$")]
    private static partial Regex ProjectPath();

    [GeneratedRegex(@"^/notes/(?<id>[0-9a-fA-F\-]{32,36})/?$")]
    private static partial Regex NotePath();

    public async Task<InternalLinkDto> ResolveAsync(Uri uri, Guid viewerId, CancellationToken ct)
    {
        var path = uri.AbsolutePath;
        var display = PathAndQuery(uri);
        var page = new InternalLinkDto("page", null, null, null, display);

        var m = NewsPath().Match(path);
        if (m.Success && Guid.TryParse(m.Groups["id"].Value, out var newsId))
        {
            if (!await perms.IsAllowedAsync(viewerId, "news.read", null, ct)) return page;
            var post = await news.GetAsync(newsId, viewerId, ct);
            if (post is null) return page;
            var subtitle = post.Type switch
            {
                "memo" => "Memo",
                "announcement" => "Announcement",
                "condolence" or "duka" => "Condolence",
                _ => "News",
            };
            return new InternalLinkDto("news", post.Id, post.Title, $"{subtitle} · {Application.PersonName.Display(post.AuthorName)}", display);
        }

        m = ProjectPath().Match(path);
        if (m.Success && Guid.TryParse(m.Groups["id"].Value, out var projectId))
        {
            if (!await perms.IsAllowedAsync(viewerId, "projects.read", null, ct)) return page;
            var taskParam = QueryValue(uri, "task");
            if (taskParam is not null && Guid.TryParse(taskParam, out var taskId))
            {
                var task = await projects.GetTaskAsync(taskId, viewerId, ct);
                if (task is null || task.ProjectId != projectId) return page;
                var proj = await projects.GetProjectAsync(projectId, viewerId, ct);
                var due = task.DueDate is { } d ? $" · due {d:dd MMM yyyy}" : "";
                return new InternalLinkDto("task", task.Id, task.Title, $"{proj?.Name ?? "Project"} · {StatusLabel(task.Status)}{due}", display);
            }
            var p = await projects.GetProjectAsync(projectId, viewerId, ct);
            if (p is null) return page;
            return new InternalLinkDto("project", p.Id, p.Name, $"Project · {p.Progress}% · {p.TaskCount} tasks", display);
        }

        m = NotePath().Match(path);
        if (m.Success && Guid.TryParse(m.Groups["id"].Value, out var noteId))
        {
            if (!await perms.IsAllowedAsync(viewerId, "notes.read", null, ct)) return page;
            var result = await notes.GetPageAsync(viewerId, noteId, ct);
            if (!result.Success || result.Data is not { } note) return page;
            return new InternalLinkDto("page", note.Id, note.Title, "Note", display);
        }

        return page;
    }

    private static string StatusLabel(string s) => s switch
    {
        "todo" => "To do",
        "doing" => "In progress",
        "review" => "In review",
        "done" => "Done",
        _ => s,
    };

    private static string PathAndQuery(Uri uri) => uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;

    private static string? QueryValue(Uri uri, string key)
    {
        var q = uri.Query.TrimStart('?');
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && string.Equals(Uri.UnescapeDataString(kv[0]), key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }
}
