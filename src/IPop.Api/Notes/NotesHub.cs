using IPop.Application.Notes;
using IPop.Application.Rbac;
using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Notes;

/// Live co-editing for Notes. Clients join "note:{pageId}"; Yjs document updates are stored then
/// relayed to everyone else on the page, awareness (cursors, who is here) is relayed only.
/// Access is re-checked on every call, so removing a share stops edits immediately.
public class NotesHub(INoteService notes, IPermissionEvaluator perms) : Hub
{
    public static string Group(Guid pageId) => $"note:{pageId}";

    /// Who is in each page's group (page -> connection -> user), so losing access can take the connection out of it:
    /// otherwise an open tab kept receiving the live content after its share was removed. Per process, like groups.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, System.Collections.Concurrent.ConcurrentDictionary<string, Guid>> Members = new();

    /// Removes from the page's live group every connection whose user no longer has access.
    public static async Task EvictLostAccessAsync(IHubContext<NotesHub> hub, INoteService notes, Guid pageId, CancellationToken ct)
    {
        if (!Members.TryGetValue(pageId, out var conns)) return;
        foreach (var userId in conns.Values.Distinct().ToList())
        {
            if (await notes.GetAccessAsync(userId, pageId, ct) != NoteAccess.None) continue;
            foreach (var (connection, _) in conns.Where(c => c.Value == userId).ToList())
            {
                conns.TryRemove(connection, out _);
                await hub.Groups.RemoveFromGroupAsync(connection, Group(pageId), ct);
                await hub.Clients.Client(connection).SendAsync("noteAccessLost", pageId, ct);
            }
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var page in Members.Values) page.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task<bool> JoinPage(Guid pageId)
    {
        var userId = GetUserId();
        if (userId is null || !await perms.IsAllowedAsync(userId.Value, "notes.read", null, Context.ConnectionAborted)) return false;
        if (await notes.GetAccessAsync(userId.Value, pageId, Context.ConnectionAborted) == NoteAccess.None) return false;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(pageId), Context.ConnectionAborted);
        Members.GetOrAdd(pageId, _ => new())[Context.ConnectionId] = userId.Value;
        return true;
    }

    public async Task LeavePage(Guid pageId)
    {
        if (Members.TryGetValue(pageId, out var conns)) conns.TryRemove(Context.ConnectionId, out _);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(pageId), Context.ConnectionAborted);
        await Clients.OthersInGroup(Group(pageId)).SendAsync("noteLeft", pageId, Context.ConnectionId);
    }

    /// Returns the stored update id (the client lists merged ids when snapshotting), or null if refused.
    public async Task<long?> PushUpdate(Guid pageId, string data)
    {
        var userId = GetUserId();
        if (userId is null || !await perms.IsAllowedAsync(userId.Value, "notes.write", null, Context.ConnectionAborted)) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { return null; }
        var r = await notes.AppendUpdateAsync(userId.Value, pageId, bytes, Context.ConnectionAborted);
        if (!r.Success) return null;
        await Clients.OthersInGroup(Group(pageId)).SendAsync("noteUpdate", new { pageId, id = r.Data, data });
        return r.Data;
    }

    public async Task Awareness(Guid pageId, string data)
    {
        if (data.Length > 16 * 1024) return;
        var userId = GetUserId();
        if (userId is null || await notes.GetAccessAsync(userId.Value, pageId, Context.ConnectionAborted) == NoteAccess.None) return;
        await Clients.OthersInGroup(Group(pageId)).SendAsync("noteAwareness", new { pageId, data, connectionId = Context.ConnectionId });
    }

    private Guid? GetUserId()
    {
        var sub = Context.User?.FindFirst("sub")?.Value
            ?? Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
