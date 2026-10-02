using IPop.Web.Models;

namespace IPop.Web.Services;

public enum DraftSettleResult { None, Removed, KeptNewer, KeptFailed, KeptUnknown }

/// Ties composer sends to the draft version they consumed (Chat.Drafts.cs). The draft keeps
/// the sent text until the server confirms that exact (room, idempotency key); only then is
/// it removed, and only if nothing was typed since. Failed/unknown sends leave it in place so
/// a reload or navigation still has the text. In memory only: no durable send queue.
public sealed class DraftSendLedger
{
    private sealed class Entry(DraftSnapshot snap, IReadOnlyList<PendingAttachmentItem> attachments)
    {
        public DraftSnapshot Snap { get; } = snap;
        public IReadOnlyList<PendingAttachmentItem> Attachments { get; } = attachments;
        public Guid? TempId { get; set; }
        public SendState State { get; set; } = SendState.Pending;
        public bool Warned { get; set; }
    }

    private readonly Dictionary<(Guid Room, Guid Key), Entry> _sends = [];

    public int Count => _sends.Count;

    public void Started(Guid roomId, Guid key, DraftSnapshot snapshot, IReadOnlyList<PendingAttachmentItem>? attachments = null) =>
        _sends[(roomId, key)] = new Entry(snapshot, attachments ?? []);

    /// Applies a send outcome (SendSettled or the awaited return value; idempotent).
    public DraftSettleResult Settled(SendOutcome o, DraftStore drafts)
    {
        if (!_sends.TryGetValue((o.RoomId, o.IdempotencyKey), out var e)) return DraftSettleResult.None;
        if (o.TempId != Guid.Empty) e.TempId = o.TempId;
        e.State = o.State;
        switch (o.State)
        {
            case SendState.Confirmed:
                _sends.Remove((o.RoomId, o.IdempotencyKey));
                return drafts.RemoveIfMatches(e.Snap) ? DraftSettleResult.Removed : DraftSettleResult.KeptNewer;
            case SendState.Failed:
                return DraftSettleResult.KeptFailed;
            case SendState.Unknown:
                if (e.Warned) return DraftSettleResult.None;
                e.Warned = true;
                return DraftSettleResult.KeptUnknown;
            default:
                return DraftSettleResult.None;
        }
    }

    /// True while a send from this room is unsettled/failed and the stored draft is still the
    /// text it sent: an empty composer must not erase it, and opening the room must not paste
    /// it back next to its own bubble.
    public bool HoldsDraft(Guid roomId, DraftStore drafts) =>
        _sends.Any(kv => kv.Key.Room == roomId && drafts.IsCurrent(kv.Value.Snap));

    /// Removes and returns the attachments recorded for a send (for restoring a failed one).
    public IReadOnlyList<PendingAttachmentItem>? Take(Guid tempId)
    {
        var hit = _sends.FirstOrDefault(kv => kv.Value.TempId == tempId);
        if (hit.Value is null) return null;
        _sends.Remove(hit.Key);
        return hit.Value.Attachments;
    }

    public void ForgetRoom(Guid roomId)
    {
        foreach (var k in _sends.Keys.Where(k => k.Room == roomId).ToList()) _sends.Remove(k);
    }

    public void Clear() => _sends.Clear();
}
