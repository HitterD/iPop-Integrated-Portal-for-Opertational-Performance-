namespace IPop.Core.Entities;

/// <summary>
/// A Notes page (Notion-like). The body is a Yjs document: <see cref="YState"/> holds the compacted
/// state and <see cref="NoteUpdate"/> rows hold updates not yet folded into it (Yjs updates are
/// idempotent, so loading = state + every remaining update). Access is the owner plus
/// <see cref="NoteShare"/> rows on this page or any ancestor; sub-pages inherit and can only add.
/// Style keys (Cover/Accent/Font) are palette names, resolved to colours by the client.
/// </summary>
public class NotePage
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? ParentId { get; set; }
    public required string Title { get; set; }
    public string? Emoji { get; set; }
    public string? Cover { get; set; }
    public required string Accent { get; set; }
    public required string Font { get; set; }
    public bool FullWidth { get; set; }
    public bool SmallText { get; set; }
    /// Manual position among its siblings (1..n once someone reorders them); 0 = never placed, which falls
    /// back to the default order (sub-pages by title, top-level pages newest first).
    public int SortOrder { get; set; }
    /// The per-user tutorial page ("Mulai di sini").
    public bool IsGuide { get; set; }
    /// Template the first editor fills an empty page with ("guide", "meeting", ...); cleared once claimed.
    public string? SeedTemplate { get; set; }
    public byte[]? YState { get; set; }
    /// Bumped by every snapshot; a snapshot built on an older version is refused (the client refetches,
    /// merges and retries), so one client can never drop edits it had not seen yet.
    public long StateVersion { get; set; }
    /// Users whose edits landed since the last version (comma-separated ids).
    public string? PendingContributors { get; set; }
    public string? Excerpt { get; set; }
    public string? PlainText { get; set; }
    public int TodoDone { get; set; }
    public int TodoTotal { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedById { get; set; }

    public User Owner { get; set; } = default!;
    public NotePage? Parent { get; set; }
}

/// One Yjs update relayed through the notes hub. Deleted once a snapshot from a client that had
/// merged it is accepted (the client lists the ids it merged).
public class NoteUpdate
{
    public long Id { get; set; }
    public Guid PageId { get; set; }
    public Guid UserId { get; set; }
    public required byte[] Data { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// Access to a page for one user or for every current member of a chat room.
/// Level: "view" | "comment" | "edit".
public class NoteShare
{
    public Guid Id { get; set; }
    public Guid PageId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? RoomId { get; set; }
    public required string Level { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }

    public NotePage Page { get; set; } = default!;
    public User? User { get; set; }
    public ChatRoom? Room { get; set; }
}

public class NoteFavorite
{
    public Guid PageId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class NoteVisit
{
    public Guid PageId { get; set; }
    public Guid UserId { get; set; }
    public DateTime VisitedAt { get; set; }
}

/// A point-in-time copy of the page body for version history.
public class NoteVersion
{
    public Guid Id { get; set; }
    public Guid PageId { get; set; }
    public required byte[] State { get; set; }
    public required string Title { get; set; }
    /// Comma-separated user ids who edited since the previous version.
    public string? Contributors { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// A comment thread on a text range. Anchored with Yjs relative positions (not a document mark)
/// so people with comment-only access never need to write to the document.
public class NoteThread
{
    public Guid Id { get; set; }
    public Guid PageId { get; set; }
    public string? Quote { get; set; }
    /// Yjs relative positions {"from":b64,"to":b64} so the highlight follows edits; null = page-level.
    public string? Anchor { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedById { get; set; }

    public List<NoteComment> Comments { get; set; } = [];
}

public class NoteComment
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    /// Null for the built-in "iPop Guide" example comment on the tutorial page.
    public Guid? AuthorId { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; }

    public NoteThread Thread { get; set; } = default!;
    public User? Author { get; set; }
}

/// Links an attachment (area "notes") to the page it was uploaded into.
public class NoteAttachment
{
    public Guid AttachmentId { get; set; }
    public Guid PageId { get; set; }
}
