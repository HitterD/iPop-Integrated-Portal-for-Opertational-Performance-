namespace IPop.Application.Chat.Mentions;

public abstract record MentionSegment
{
    public sealed record Text(string Value) : MentionSegment;
    public sealed record User(Guid UserId, string Name) : MentionSegment;
    public sealed record All : MentionSegment;

    // ipop-22: Project / Task interconnect mentions (p:GUID / t:GUID)
    public sealed record Project(Guid ProjectId, string Name) : MentionSegment;
    public sealed record Task(Guid TaskId, string Title) : MentionSegment;
}
