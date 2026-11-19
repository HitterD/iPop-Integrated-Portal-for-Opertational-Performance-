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
    /// "page" (a document) or "board" (a whiteboard: one endless canvas, no document text). Fixed when the page is created.
    public string Kind { get; set; } = "page";
    /// Locked pages reject every edit (text, drawings, style, title) until someone with edit access unlocks them.
    public DateTime? LockedAt { get; set; }
    public Guid? LockedById { get; set; }
    /// Photo cover: a notes attachment linked to this page. When set it wins over <see cref="Cover"/> (a gradient key).
    public Guid? CoverImageId { get; set; }
    /// Vertical focus of the photo inside the cover band, 0 (top) .. 100 (bottom).
    public int CoverPosY { get; set; } = 50;
    /// Whiteboards only: a small picture of the board (a notes attachment linked to this page) shown on the Whiteboards list.
    /// Replaced, never accumulated: the previous one is deleted when a new one is saved.
    public Guid? ThumbnailId { get; set; }
    public DateTime? ThumbnailAt { get; set; }
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
    /// Set on pages created by Import: where the content came from ("notion" | "markdown" | "html"), and a
    /// stable key for the source page ("notion:<id>" or a file hash) so a second import can warn.
    public string? ImportSource { get; set; }
    public string? ImportKey { get; set; }
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
    /// A name someone gave this version ("Before the audit"). A named version is never thinned out by the cleanup.
    public string? Name { get; set; }
    public Guid? NamedById { get; set; }
    /// A name someone gave this version ("Before the audit"). A named version is never thinned out by the cleanup.
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
    /// Set when the author edits the comment (shown as "edited").
    public DateTime? EditedAt { get; set; }

    public NoteThread Thread { get; set; } = default!;
    public User? Author { get; set; }
    public List<NoteCommentReaction> Reactions { get; set; } = [];
}

/// A page the owner saved as their own template: the content of the page at that moment, ready to start new pages from.
/// Private to the owner. The attachments referenced inside are cloned when a page is started from it.
public class NoteTemplate
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Emoji { get; set; }
    public string Accent { get; set; } = "blue";
    public string Kind { get; set; } = "page";
    public required byte[] State { get; set; }
    public string? Excerpt { get; set; }
    public Guid? SourcePageId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// Who opened a page and when they last did. Only the owner and editors see this list. One row per person per page.
/// A group of whiteboard elements someone saved to reuse ("My library"). `Shared` puts it in the team library, which every
/// iPop user sees in the board's Library panel; only the owner can rename or remove it. `Json` is the elements, already
/// moved to start at 0,0 (at most 256 KB).
/// A whiteboard someone saved as a template (round 6 Template Center). Private ("My templates") unless `Shared`, which puts it
/// in Team templates for every iPop user. The owner can rename, share/unshare or remove it; an admin (notes.admin) can also
/// remove a shared one. `Json` is the elements, moved to start at 0,0 (at most 512 KB); the preview is drawn from it.
public class NoteBoardTemplate
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Json { get; set; }
    public int Count { get; set; }
    public bool Shared { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class NoteLibraryItem
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public required string Json { get; set; }
    public int Count { get; set; }
    public bool Shared { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class NoteView
{
    public Guid PageId { get; set; }
    public Guid UserId { get; set; }
    public DateTime LastViewedAt { get; set; }
    public int Count { get; set; }
}

/// "This page links to that page", as last seen in the saved content (inline page links and sub-page blocks are not
/// included: only explicit page links). Rebuilt from every snapshot; it powers the "Linked from" list on the target.
public class NoteLink
{
    public Guid FromPageId { get; set; }
    public Guid ToPageId { get; set; }
}

/// A to-do with a due date, as last seen in the page's saved content. Rebuilt from every snapshot (the server does not
/// read the Yjs document itself), and used by <c>NotesReminderWorker</c> to remind the assignee the day before and on the
/// day. Reminders that were already sent stay sent when the same item is saved again.
public class NoteTodoDue
{
    public Guid PageId { get; set; }
    /// Stable key of the item inside the page (client-generated from its assignee, due date and text).
    public required string ItemKey { get; set; }
    public Guid? AssigneeId { get; set; }
    public DateOnly Due { get; set; }
    public required string Text { get; set; }
    public bool Done { get; set; }
    public DateTime? DayBeforeSentAt { get; set; }
    public DateTime? DaySentAt { get; set; }
    /// The Projects task this to-do was turned into ("→ Task"); reported with each save. Null = no task. (Not a foreign key: a task
    /// that is deleted in Projects must not block saving the note.)
    public Guid? TaskId { get; set; }
}

/// One emoji by one person on one comment. A person has at most one row per emoji per comment (toggle).
public class NoteCommentReaction
{
    public Guid CommentId { get; set; }
    public Guid UserId { get; set; }
    public required string Emoji { get; set; }
    public DateTime CreatedAt { get; set; }

    public NoteComment Comment { get; set; } = default!;
}

/// Links an attachment (area "notes") to the page it was uploaded into.
public class NoteAttachment
{
    public Guid AttachmentId { get; set; }
    public Guid PageId { get; set; }
}

/// "Repeat…" on a page: a new copy of the page is made on a schedule. The page is the template: each run copies its content as it is at
/// that moment into a new page next to it (same parent, so the same people can open it), titled "Name — 24 Nov 2026", owned by whoever set
/// the repeat up. At most one page per rule per local day (<see cref="LastCreatedFor"/>).
public class NoteRecurrence
{
    public Guid Id { get; set; }
    /// The page that is copied (the template). Deleting it deletes the rule.
    public Guid PageId { get; set; }
    /// Who set it up: the owner of every page it makes, and the person asked to be able to edit the template.
    public Guid OwnerId { get; set; }
    /// RRULE subset: FREQ=DAILY|WEEKLY|MONTHLY;INTERVAL;BYDAY;BYMONTHDAY. "Workdays" is WEEKLY;BYDAY=MO,TU,WE,TH,FR.
    public required string Rule { get; set; }
    /// First day this rule may create a page (the day it was set up, in local time): never back-fills missed days.
    public DateOnly StartsOn { get; set; }
    public bool Enabled { get; set; } = true;
    /// The local day the last page was made for (null = none yet). A run never makes a second page for the same day.
    public DateOnly? LastCreatedFor { get; set; }
    public Guid? LastPageId { get; set; }
    public DateTime CreatedAt { get; set; }
}
