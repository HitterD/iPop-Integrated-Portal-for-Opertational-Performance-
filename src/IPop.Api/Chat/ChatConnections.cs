using System.Collections.Concurrent;

namespace IPop.Api.Chat;

/// Live ChatHub connection ids per user, so the server can pull a removed member's sockets out of
/// a room group itself instead of trusting their client to call LeaveRoom. Per process: the API
/// runs as one node; with more nodes each node still removes the connections it holds.
public static class ChatConnections
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> ByUser = new();

    public static void Add(Guid userId, string connectionId)
        => ByUser.GetOrAdd(userId, _ => new()).TryAdd(connectionId, 0);

    public static void Remove(Guid userId, string connectionId)
    {
        if (!ByUser.TryGetValue(userId, out var set)) return;
        set.TryRemove(connectionId, out _);
        if (set.IsEmpty) ByUser.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<string, byte>>(userId, set));
    }

    public static IReadOnlyList<string> Of(Guid userId)
        => ByUser.TryGetValue(userId, out var set) ? [.. set.Keys] : [];
}
