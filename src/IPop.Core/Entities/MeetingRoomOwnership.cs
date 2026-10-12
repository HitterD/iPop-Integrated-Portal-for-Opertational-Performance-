namespace IPop.Core.Entities;

/// Immutable takeover ledger: edits/deletes of Meetings never erase who already held a physical room.
/// A booking may claim a room once; the latest claim is its persistent high-water mark.
public sealed class MeetingRoomOwnership
{
    public long Id { get; set; }
    public string RoomKey { get; set; } = "";
    public Guid MeetingId { get; set; }
    public DateTime WindowStartAt { get; set; }
    public DateTime ClaimedAt { get; set; }
}
