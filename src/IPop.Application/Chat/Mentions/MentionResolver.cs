namespace IPop.Application.Chat.Mentions;

public sealed record MentionResolutionInput(
    Guid SenderId,
    string RoomType,
    IReadOnlyList<MentionSegment> Segments,
    IReadOnlyDictionary<Guid, bool> RoomMembers);

public sealed record MentionRecipient(Guid UserId, bool ViaAllOnly, bool SuppressNotification);

public static class MentionResolver
{
    public static IReadOnlyList<MentionRecipient> Resolve(MentionResolutionInput input)
    {
        var seen = new HashSet<Guid>();
        var individual = new HashSet<Guid>();
        bool hasAll = false;
        foreach (var s in input.Segments)
        {
            if (s is MentionSegment.User u) individual.Add(u.UserId);
            else if (s is MentionSegment.All) hasAll = true;
        }
        var result = new List<MentionRecipient>();
        foreach (var uid in individual)
        {
            if (uid == input.SenderId) continue;
            if (!input.RoomMembers.TryGetValue(uid, out var active)) continue;
            if (seen.Add(uid))
                result.Add(new MentionRecipient(uid, false, !active));
        }
        if (hasAll && input.RoomType == "group")
        {
            foreach (var (uid, active) in input.RoomMembers)
            {
                if (uid == input.SenderId) continue;
                if (seen.Add(uid))
                    result.Add(new MentionRecipient(uid, true, !active));
            }
        }
        if (result.Count > 255) result = result.Take(255).ToList();
        return result;
    }
}
