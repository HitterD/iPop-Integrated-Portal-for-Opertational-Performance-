namespace IPop.Core.Entities;

/// <summary>
/// A confidential, cross-department topic in The Hub (docs/superpowers/specs/2026-11-21-the-hub-design.md).
/// Only members can read it. Status is derived: Solved when <see cref="SolvedAt"/> is set, otherwise Ongoing
/// once someone other than the starter has replied, otherwise New.
/// </summary>
public class HubTopic
{
    public Guid Id { get; set; }
    /// problem | clarity | idea | change | buy | other
    public required string Kind { get; set; }
    public required string Title { get; set; }
    /// "What do you need from them?"
    public required string Need { get; set; }
    /// Answer-by date (a calendar day, stored as UTC midnight).
    public DateTime? DueDate { get; set; }
    public Guid CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// Last reply / system event; orders the list.
    public DateTime LastActivityAt { get; set; }
    /// True once someone other than the starter has replied (New → Ongoing).
    public bool HasReply { get; set; }
    public DateTime? SolvedAt { get; set; }
    public Guid? SolvedById { get; set; }
    /// The reply marked "Best answer": shown under the opening post and used to pre-fill Mark as solved.
    public Guid? BestPostId { get; set; }
    public string? SolvedSummary { get; set; }
    public DateTime? DeletedAt { get; set; }
}

public class HubMember
{
    public Guid TopicId { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid? AddedById { get; set; }
    public DateTime AddedAt { get; set; }
    /// Everything up to here is read; null = never opened.
    public DateTime? LastReadAt { get; set; }
}

public class HubPost
{
    public Guid Id { get; set; }
    public Guid TopicId { get; set; }
    /// Null for a system line ("X added Y", "marked solved").
    public Guid? AuthorId { get; set; }
    public User? Author { get; set; }
    /// The reply this one answers (one level: replies to a reply hang under the same top post).
    public Guid? ParentId { get; set; }
    public required string Body { get; set; }
    /// reply | system
    public required string Kind { get; set; }
    public DateTime CreatedAt { get; set; }
    /// Set when the author changed the text ("edited").
    public DateTime? EditedAt { get; set; }
    /// Soft delete by the author: the thread keeps a "This reply was deleted" line so replies under it still make sense.
    public DateTime? DeletedAt { get; set; }
}

/// One-tap "Noted": the person has read the reply and agrees / has nothing to add.
public class HubNote
{
    public Guid PostId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// A tracked question: "Needs an answer from X". Answered when X replies in the topic after it was asked.
public class HubAsk
{
    public Guid Id { get; set; }
    public Guid TopicId { get; set; }
    public Guid PostId { get; set; }
    public Guid ToUserId { get; set; }
    public User? ToUser { get; set; }
    public Guid AskedById { get; set; }
    public required string Question { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public Guid? AnsweredPostId { get; set; }
    public DateTime? LastRemindedAt { get; set; }
    /// The day-3 reminder went to the person's department head (without the topic text).
    public DateTime? HeadToldAt { get; set; }
}

public class HubAttachment
{
    public Guid TopicId { get; set; }
    public Guid AttachmentId { get; set; }
    public Guid? PostId { get; set; }
    public Guid UploaderId { get; set; }
    public DateTime CreatedAt { get; set; }
}
