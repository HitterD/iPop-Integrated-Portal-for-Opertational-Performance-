using IPop.Application.Files;
using IPop.Application.Rbac;
using IPop.Application.Search;
using Microsoft.Extensions.DependencyInjection;

namespace IPop.Infrastructure.Search.Providers;

/// Files: file/folder names on every plant NAS the user has connected, with their own credential and the
/// shares that credential can list (no content indexing). FileStation search is a task on the NAS: it is
/// started, polled within a time budget and always stopped afterwards, whether finished, timed out or
/// cancelled. No credential anywhere = "Connect to search", not "no results".
public sealed class FilesSearchProvider(IFileAccessService files, IPermissionEvaluator? perms = null, TimeSpan? budget = null,
    IServiceScopeFactory? scopes = null) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Files, "Files", "folder", 100);

    public static readonly string[] Servers = ["spj", "krw", "jtb", "smg"];
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(4);
    private const int MaxPerServer = 200;

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => SearchPaging.AllowedAsync(perms, userId, "files.read", ct);

    private sealed record Found(string Server, SynoItem Item);

    private enum Outcome { Done, Capped, Slow, Failed }
    private sealed record ServerResult(string Server, List<Found> Found, Outcome Outcome);

    private static async Task<ServerResult[]> SequentialAsync(List<string> servers, Func<string, Task<ServerResult>> one)
    {
        var all = new List<ServerResult>();
        foreach (var s in servers) all.Add(await one(s));
        return [.. all];
    }

    private static async Task<ServerResult> SearchOneAsync(IFileAccessService files, SearchQuery q, string server, int want,
        DateTime deadline, CancellationToken searchToken, CancellationToken ct)
    {
        var found = new List<Found>();
        if (searchToken.IsCancellationRequested) return new(server, found, Outcome.Slow);
        string? taskId = null;
        try
        {
            var shares = await files.ListSharesAsync(q.UserId, server, searchToken);
            if (shares.Count == 0) return new(server, found, Outcome.Done);
            taskId = await files.SearchStartAsync(q.UserId, server, shares, q.Text.Trim(), searchToken);
            while (true)
            {
                var page = await files.SearchListAsync(q.UserId, server, taskId, 0, want, searchToken);
                var batch = page.Items.Where(i => !i.Path.Contains("/#recycle", StringComparison.OrdinalIgnoreCase)).ToList();
                if (page.Finished || batch.Count >= want)
                {
                    found.AddRange(batch.Select(i => new Found(server, i)));
                    return new(server, found, page.Total > batch.Count ? Outcome.Capped : Outcome.Done);
                }
                if (DateTime.UtcNow >= deadline)
                {
                    found.AddRange(batch.Select(i => new Found(server, i)));
                    return new(server, found, Outcome.Slow);
                }
                await Task.Delay(250, searchToken);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(server, found, Outcome.Slow); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(server, found, Outcome.Failed); }
        finally
        {
            if (taskId is not null)
            {
                // Clean up even when the caller went away: a short, independent stop.
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await files.SearchStopAsync(q.UserId, server, taskId, stopCts.Token); } catch { }
            }
        }
    }

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var creds = await files.ListCredentialsAsync(q.UserId, ct);
        var connected = Servers.Where(s => creds.Any(c => c.Server.Equals(s, StringComparison.OrdinalIgnoreCase) && !c.IsInvalid)).ToList();
        if (connected.Count == 0)
            return SearchPage.Unavailable(Info.Key, "Connect to a file server to search file names.", q.Page, q.PageSize, "/files");

        var found = new List<Found>();
        var failed = new List<string>();
        var slow = new List<string>();
        var capped = new List<string>();
        var duration = budget ?? DefaultBudget;
        var deadline = DateTime.UtcNow + duration;
        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        searchCts.CancelAfter(duration);
        var searchToken = searchCts.Token;
        var want = Math.Min(MaxPerServer, q.Page * q.PageSize);
        // Every NAS at the same time, each with its own scope (FileAccessService holds a DbContext, so never one
        // instance in parallel). One after another, the first slow plant used up the whole budget and the others were
        // never searched. Without a scope factory (tests) it falls back to one after another.
        var results = scopes is null
            ? await SequentialAsync(connected, server => SearchOneAsync(files, q, server, want, deadline, searchToken, ct))
            : await Task.WhenAll(connected.Select(async server =>
            {
                await using var scope = scopes.CreateAsyncScope();
                var own = scope.ServiceProvider.GetRequiredService<IFileAccessService>();
                return await SearchOneAsync(own, q, server, want, deadline, searchToken, ct);
            }));
        foreach (var r in results)
        {
            found.AddRange(r.Found);
            switch (r.Outcome)
            {
                case Outcome.Failed: failed.Add(r.Server); break;
                case Outcome.Slow: slow.Add(r.Server); break;
                case Outcome.Capped: capped.Add(r.Server); break;
            }
        }

        if (failed.Count == connected.Count)
            return SearchPage.Error(Info.Key, $"File server{(failed.Count > 1 ? "s" : "")} {string.Join(", ", failed.Select(Upper))} did not answer.", q.Page, q.PageSize);

        var ranked = found
            .OrderBy(f => SearchMatch.Rank(SearchMatch.Classify(f.Item.Name, q.Lower)))
            .ThenBy(f => f.Item.IsDir ? 0 : 1).ThenByDescending(f => f.Item.Mtime);
        var (items, total) = SearchPaging.Page(ranked, q.Skip, q.PageSize);
        var hits = items.Select(f =>
        {
            var dir = ParentOf(f.Item.Path);
            return new SearchHit($"{f.Server}:{f.Item.Path}", Info.Key, f.Item.Name, $"{Upper(f.Server)} · {dir}", null,
                $"/files?server={f.Server}&path={Uri.EscapeDataString(f.Item.IsDir ? f.Item.Path : dir)}",
                f.Item.IsDir ? "folder" : "file", f.Item.Mtime > 0 ? DateTimeOffset.FromUnixTimeSeconds(f.Item.Mtime).UtcDateTime : null,
                SearchMatch.Classify(f.Item.Name, q.Lower));
        }).ToList();
        var notes = new List<string>();
        if (capped.Count > 0) notes.Add($"More matches on {string.Join(", ", capped.Select(Upper))}; refine the query or continue in Files.");
        if (failed.Count > 0) notes.Add($"{string.Join(", ", failed.Select(Upper))} did not answer.");
        if (slow.Count > 0) notes.Add($"{string.Join(", ", slow.Distinct().Select(Upper))} still searching — results may be incomplete.");
        var unconnected = Servers.Except(connected).ToList();
        if (unconnected.Count > 0) notes.Add($"Not connected: {string.Join(", ", unconnected.Select(Upper))}.");
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize, notes.Count > 0 ? string.Join(" ", notes) : null, partial: failed.Count > 0 || slow.Count > 0 || capped.Count > 0);
    }

    private static string Upper(string s) => s.ToUpperInvariant();
    private static string ParentOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }
}
