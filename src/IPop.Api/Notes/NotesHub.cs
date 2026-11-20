using System.Collections.Concurrent;
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

    /// Marker in a PushUpdate HubException: the server will never store this update, so the client must not retry it.
    public const string RefusedPrefix = "note_refused:";

    /// Who is in each page's group (page -> connection -> user), so losing access can take the connection out of it:
    /// otherwise an open tab kept receiving the live content after its share was removed. Per process, like groups.
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, Guid>> Members = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> ConnPages = new();
    private static readonly ConcurrentDictionary<string, RateBucket> UpdateRates = new();
    private static readonly ConcurrentDictionary<string, RateBucket> AwarenessRates = new();

    private sealed class RateBucket
    {
        public readonly object Gate = new();
        public long WindowStartTicks;
        public int Count;
    }

    /// Hook installed at startup (<see cref="NotesEvictionBus"/>) to broadcast eviction across API pods via Redis.
    public static Func<Guid, CancellationToken, Task>? PublishEviction { get; set; }

    /// Broadcasts eviction locally and across all pods (when Redis bus is active).
    public static async Task EvictAllPodsAsync(IHubContext<NotesHub> hub, INoteService notes, Guid pageId, CancellationToken ct, IPermissionEvaluator? perms = null)
    {
        await EvictLostAccessAsync(hub, notes, pageId, ct, perms);
        if (PublishEviction is { } pub) await pub(pageId, ct);
    }

    public static Task EvictLostAccessAsync(IHubContext<NotesHub> hub, INoteService notes, Guid pageId, CancellationToken ct) =>
        EvictLostAccessAsync(hub, notes, pageId, ct, null);

    /// Removes from its live group every connection whose user no longer has access. Access is inherited down the tree, so
    /// revoking a share on a parent, moving a page or trashing a parent changes the access to pages BENEATH it as well:
    /// every page that currently has live connections is re-checked, not only the one the request named.
    public static async Task EvictLostAccessAsync(IHubContext<NotesHub> hub, INoteService notes, Guid pageId, CancellationToken ct, IPermissionEvaluator? perms)
    {
        List<(Guid Page, ConcurrentDictionary<string, Guid> Conns)> snapshot;
        lock (Members) snapshot = Members.Select(kv => (kv.Key, kv.Value)).ToList();

        foreach (var (page, conns) in snapshot)
        {
            List<Guid> userIds;
            lock (Members) userIds = conns.Values.Distinct().ToList();
            foreach (var userId in userIds)
            {
                var allowed = (perms is null || await perms.IsAllowedAsync(userId, "notes.read", null, ct))
                    && await notes.GetAccessAsync(userId, page, ct) != NoteAccess.None;
                if (allowed) continue;

                List<string> evicted;
                lock (Members)
                {
                    evicted = conns.Where(c => c.Value == userId).Select(c => c.Key).ToList();
                    foreach (var conn in evicted) RemoveMembershipLocked(page, conn);
                }
                foreach (var connection in evicted)
                {
                    await hub.Groups.RemoveFromGroupAsync(connection, Group(page), ct);
                    await hub.Clients.Client(connection).SendAsync("noteAccessLost", page, ct);
                    try { await hub.Clients.Group(Group(page)).SendAsync("noteLeft", page, connection, ct); }
                    catch { /* best-effort */ }
                }
            }
        }
    }

    private static bool RemoveMembershipLocked(Guid pageId, string connectionId)
    {
        if (ConnPages.TryGetValue(connectionId, out var pages))
        {
            pages.TryRemove(pageId, out _);
            if (pages.IsEmpty) ConnPages.TryRemove(connectionId, out _);
        }
        if (!Members.TryGetValue(pageId, out var conns) || !conns.TryRemove(connectionId, out _)) return false;
        if (conns.IsEmpty) Members.TryRemove(pageId, out _);
        return true;
    }

    private static bool AllowRate(ConcurrentDictionary<string, RateBucket> map, string connectionId, int limit)
    {
        var bucket = map.GetOrAdd(connectionId, _ => new RateBucket());
        var now = Environment.TickCount64;
        lock (bucket.Gate)
        {
            if (now - bucket.WindowStartTicks >= 5000)
            {
                bucket.WindowStartTicks = now;
                bucket.Count = 0;
            }
            if (bucket.Count >= limit) return false;
            bucket.Count++;
            return true;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        List<Guid>? leftPages = null;
        lock (Members)
        {
            if (ConnPages.TryRemove(Context.ConnectionId, out var pages))
            {
                leftPages = [.. pages.Keys];
                foreach (var pageId in leftPages)
                {
                    if (Members.TryGetValue(pageId, out var conns))
                    {
                        conns.TryRemove(Context.ConnectionId, out _);
                        if (conns.IsEmpty) Members.TryRemove(pageId, out _);
                    }
                }
            }
        }
        UpdateRates.TryRemove(Context.ConnectionId, out _);
        AwarenessRates.TryRemove(Context.ConnectionId, out _);
        if (leftPages is not null)
            foreach (var pageId in leftPages)
                try { await Clients.OthersInGroup(Group(pageId)).SendAsync("noteLeft", pageId, Context.ConnectionId); }
                catch { /* best-effort cursor cleanup */ }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<bool> JoinPage(Guid pageId)
    {
        var userId = GetUserId();
        if (userId is null || !await perms.IsAllowedAsync(userId.Value, "notes.read", null, Context.ConnectionAborted)) return false;
        if (await notes.GetAccessAsync(userId.Value, pageId, Context.ConnectionAborted) == NoteAccess.None) return false;

        lock (Members)
        {
            Members.GetOrAdd(pageId, _ => new())[Context.ConnectionId] = userId.Value;
            ConnPages.GetOrAdd(Context.ConnectionId, _ => new())[pageId] = 0;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(pageId), Context.ConnectionAborted);

        // Re-check after joining so concurrent revocation/trash never leaves an un-evictable session.
        if (!await perms.IsAllowedAsync(userId.Value, "notes.read", null, Context.ConnectionAborted)
            || await notes.GetAccessAsync(userId.Value, pageId, Context.ConnectionAborted) == NoteAccess.None)
        {
            lock (Members) RemoveMembershipLocked(pageId, Context.ConnectionId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(pageId), CancellationToken.None);
            return false;
        }
        return true;
    }

    public async Task LeavePage(Guid pageId)
    {
        bool wasMember;
        lock (Members) wasMember = RemoveMembershipLocked(pageId, Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(pageId), Context.ConnectionAborted);
        if (wasMember) await Clients.OthersInGroup(Group(pageId)).SendAsync("noteLeft", pageId, Context.ConnectionId);
    }

    /// Returns the stored update id (the client lists merged ids when snapshotting), or null if refused.
    public async Task<long?> PushUpdate(Guid pageId, string data)
    {
        var userId = GetUserId();
        // A refusal is final (no access, or an update the server will never take): it throws a HubException naming the
        // reason, so the editor stops instead of retrying every 3 s forever behind an "Offline" badge.
        if (userId is null || !await perms.IsAllowedAsync(userId.Value, "notes.write", null, Context.ConnectionAborted))
            throw new HubException(RefusedPrefix + "forbidden");
        if (string.IsNullOrEmpty(data) || data.Length > 768 * 1024)
            throw new HubException(RefusedPrefix + "invalid_update");
        if (!AllowRate(UpdateRates, Context.ConnectionId, 80))
            throw new HubException("rate_limited");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data); }
        catch (FormatException) { throw new HubException(RefusedPrefix + "invalid_update"); }
        var r = await notes.AppendUpdateAsync(userId.Value, pageId, bytes, Context.ConnectionAborted);
        if (!r.Success) throw new HubException(RefusedPrefix + (r.Error ?? "forbidden"));
        await Clients.OthersInGroup(Group(pageId)).SendAsync("noteUpdate", new { pageId, id = r.Data, data });
        return r.Data;
    }

    public async Task Awareness(Guid pageId, string data)
    {
        if (string.IsNullOrEmpty(data) || data.Length > 16 * 1024) return;
        if (!AllowRate(AwarenessRates, Context.ConnectionId, 60)) return;
        var userId = GetUserId();
        if (userId is null || !await perms.IsAllowedAsync(userId.Value, "notes.read", null, Context.ConnectionAborted)) return;
        if (await notes.GetAccessAsync(userId.Value, pageId, Context.ConnectionAborted) == NoteAccess.None) return;
        await Clients.OthersInGroup(Group(pageId)).SendAsync("noteAwareness", new { pageId, data, connectionId = Context.ConnectionId });
    }

    private Guid? GetUserId()
    {
        var sub = Context.User?.FindFirst("sub")?.Value
            ?? Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
