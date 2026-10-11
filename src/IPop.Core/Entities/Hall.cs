namespace IPop.Core.Entities;

/// One of the five SJA Hall meeting rooms. The same rooms are used by Calls & Meetings → Meeting Rooms and
/// (on sja-hall) by the 3D hall; LiveKit room "hall-{Key}".
public class HallRoom
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public int Capacity { get; set; }
    public int SortOrder { get; set; }
}
