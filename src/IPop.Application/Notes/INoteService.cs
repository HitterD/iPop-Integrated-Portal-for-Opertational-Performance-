namespace IPop.Application.Notes;

/// Effective access to a page: the highest of every grant on the page and its ancestors.
public enum NoteAccess { None = 0, View = 1, Comment = 2, Edit = 3, Owner = 4 }

public static class NoteLevels
{
    public const string View = "view", Comment = "comment", Edit = "edit";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { View, Comment, Edit };

    public static NoteAccess ToAccess(string level) => level switch
    {
        Edit => NoteAccess.Edit,
        Comment => NoteAccess.Comment,
        View => NoteAccess.View,
        _ => NoteAccess.None,
    };

    public static string Name(NoteAccess a) => a switch
    {
        NoteAccess.Owner => "owner",
        NoteAccess.Edit => Edit,
        NoteAccess.Comment => Comment,
        NoteAccess.View => View,
        _ => "none",
    };
}

/// Palette keys shared with the client (css/v6/notes.css and the editor bundle).
public static class NoteStyle
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "page", "board" };
    public static readonly IReadOnlySet<string> Covers = new HashSet<string>
        { "ocean", "sunset", "lavender", "rose", "teal", "green", "sand", "graphite", "mesh", "aurora", "night" };
    public static readonly IReadOnlySet<string> Accents = new HashSet<string>
        { "blue", "indigo", "purple", "pink", "red", "orange", "yellow", "green", "teal", "gray" };
    public static readonly IReadOnlySet<string> Fonts = new HashSet<string> { "default", "serif", "mono" };
    public static readonly IReadOnlySet<string> Templates = new HashSet<string> { "guide", "meeting", "todo", "brief", "sop", "inspection", "weekly", "incident" };
}

public static class NoteImport
{
    public const int MaxPages = 300;
    public static readonly IReadOnlySet<string> Sources = new HashSet<string> { "notion", "markdown", "html" };
}

public record NoteResult<T>(T? Data, string? Error)
{
    public bool Success => Error is null;
}

/// One page in the sidebar. ParentId is null when the parent is not visible to the caller
/// (a sub-page shared on its own shows as a root).
public record NoteListItemDto(Guid Id, Guid? ParentId, string Title, string? Emoji, string Access,
    bool IsMine, bool IsShared, bool IsFavorite, bool IsGuide, int TodoDone, int TodoTotal,
    DateTime UpdatedAt, string OwnerName, int SortOrder = 0, string Kind = "page",
    /// Two lines of the page text for the Notes list cards (null for boards and empty pages).
    string? Excerpt = null,
    /// Cover for the list cards: a gradient key, or a photo (attachment id) that wins over it.
    string? Cover = null, Guid? CoverImageId = null,
    /// Whiteboards: the saved picture of the board (attachment id) and when it was taken.
    Guid? ThumbnailId = null, DateTime? ThumbnailAt = null, string? OwnerNik = null, DateTime? CreatedAt = null);

public record NoteSidebarDto(IReadOnlyList<NoteListItemDto> Pages, IReadOnlyList<Guid> Recent, int TrashCount = 0);

public record NoteCrumbDto(Guid Id, string Title, string? Emoji);

public record NotePageDto(Guid Id, Guid? ParentId, string Title, string? Emoji, string? Cover, string Accent,
    string Font, bool FullWidth, bool SmallText, bool IsGuide, string Access, bool IsFavorite, bool IsShared,
    string OwnerName, string OwnerNik, DateTime CreatedAt, DateTime UpdatedAt, string? UpdatedByName,
    int TodoDone, int TodoTotal, int OpenThreads, IReadOnlyList<NoteCrumbDto> Breadcrumb,
    IReadOnlyList<NoteCrumbDto> Children, IReadOnlyList<NoteRoomShareDto> Rooms,
    /// "notion" | "markdown" | "html" for pages created by Import (shown as a small badge), else null.
    string? ImportSource = null, Guid? CoverImageId = null, int CoverPosY = 50, string Kind = "page",
    DateTime? LockedAt = null, string? LockedByName = null);

public record NoteRoomShareDto(Guid RoomId, string Name, string Level);

public record CreateNoteRequest(Guid? ParentId, string? Title, string? Emoji, string? Template, string? Accent, string? Cover, string? Kind = null);

/// Cover: "" removes any cover; a gradient key picks that gradient (and drops a photo). CoverImageId picks a photo that is
/// already linked to this page; CoverPosY (0-100) only moves the photo.
public record UpdateNoteMetaRequest(string? Title, string? Emoji, string? Cover, string? Accent, string? Font,
    bool? FullWidth, bool? SmallText, Guid? CoverImageId = null, int? CoverPosY = null);

/// Put a page under ParentId (null = top level) just before BeforeId (null = at the end). Confirm is required
/// when re-parenting changes who inherits access to the page (the first try fails with "confirm_audience").
public record MoveNoteRequest(Guid? ParentId, Guid? BeforeId, bool Confirm = false);

public record NoteUpdateDto(long Id, string Data);

/// Everything a client needs to build the document: compacted state + updates not yet folded in.
public record NoteDocDto(string? State, long Version, IReadOnlyList<NoteUpdateDto> Updates);

/// A page that links to the current page. Only pages the viewer can open are ever listed.
public record NoteBacklinkDto(Guid Id, string Title, string? Emoji, string? Excerpt);

/// "→ Task": make a Projects task from a to-do. Everything comes from the editor (it is the person's own action; the Projects
/// service still checks they can add tasks to that project and that the assignee is a member).
public record NoteTodoTaskRequest(string Title, Guid ProjectId, string? Due, Guid? AssigneeId);
public record NoteTodoTaskDto(Guid TaskId, Guid ProjectId, string Title, string ProjectName, bool Assigned);
/// A to-do that points at a Projects task, as reported with a save: ticking it completes the task.
public record NoteTodoTaskLinkDto(Guid TaskId, bool Done);

/// What a repeat run made: the new page and everyone who can open it except its owner (they are told about the new page).
public record NoteRecurrenceRunDto(Guid PageId, string Title, IReadOnlyList<Guid> NotifyUserIds);

/// "Repeat…" on a page: what is stored, in the words the dialog shows.
public record NoteRecurrenceDto(Guid PageId, string Rule, string Description, bool Enabled, DateOnly StartsOn, DateOnly? LastCreatedFor, Guid? LastPageId);
public record SetNoteRecurrenceRequest(string Kind, IReadOnlyList<string>? Days, int? MonthDay);

public record NoteTodoDueDto(string Key, Guid? AssigneeId, string Due, string Text, bool Done, Guid? TaskId = null);

public record NoteSnapshotRequest(string State, long BaseVersion, IReadOnlyList<long> MergedUpdateIds,
    string? PlainText, int TodoDone, int TodoTotal, IReadOnlyList<NoteTodoDueDto>? TodoDue = null, IReadOnlyList<Guid>? LinksTo = null,
    IReadOnlyList<NoteTodoTaskLinkDto>? TodoTasks = null);

public record NoteSnapshotResultDto(long Version);

/// One page to create in an import. ParentKey refers to an earlier page in the same request (null = put
/// it under the request's ParentId). Content is written afterwards through the snapshot endpoint.
public record ImportNotePage(string Key, string? ParentKey, string? Title, string? Emoji, string? Cover, string? ImportKey);

public record ImportNotesRequest(Guid? ParentId, string Source, IReadOnlyList<ImportNotePage> Pages);

public record ImportedNoteDto(string Key, Guid Id);

public record ImportNotesResultDto(IReadOnlyList<ImportedNoteDto> Pages);

public record FindImportedRequest(IReadOnlyList<string> Keys);

/// A live page of the caller's that came from the same source page as something being imported now.
public record PreviousImportDto(string ImportKey, Guid Id, string Title, DateTime ImportedAt);

public record NoteVersionDto(Guid Id, DateTime CreatedAt, string Title, IReadOnlyList<string> Contributors,
    string? Name = null, bool IsCurrent = false);

public record NameVersionRequest(string? Name);

public record NoteTemplateDto(Guid Id, string Name, string? Emoji, string Accent, string Kind, string? Excerpt, DateTime CreatedAt);
public record SaveTemplateRequest(string? Name);
public record NewFromTemplateRequest(Guid? ParentId, string? Title);

public record NoteLibraryItemDto(Guid Id, string Name, string Json, int Count, bool Shared, bool Mine, string OwnerName, DateTime CreatedAt);
public record SaveLibraryItemRequest(string? Name, string Json, bool Shared = false);
public record UpdateLibraryItemRequest(string? Name, bool? Shared);

public record NoteBoardTemplateDto(Guid Id, string Name, string? Description, string Json, int Count, bool Shared, bool Mine, string OwnerName, DateTime CreatedAt);
public record SaveBoardTemplateRequest(string? Name, string? Description, string Json, bool Shared = false);
public record UpdateBoardTemplateRequest(string? Name, string? Description, bool? Shared);

public record NoteViewerDto(Guid UserId, string Name, string? Nik, DateTime LastViewedAt, int Count);


public record NoteShareDto(Guid Id, Guid? UserId, string? UserName, string? UserNik, string? UserPosition,
    Guid? RoomId, string? RoomName, string? RoomType, int RoomMembers, string Level,
    /// Set when the grant comes from an ancestor page (shown read-only: children cannot remove it).
    Guid? InheritedFromId, string? InheritedFromTitle);

public record NoteSharesDto(Guid OwnerId, string OwnerName, string OwnerNik, string? OwnerPosition, IReadOnlyList<NoteShareDto> Shares);

public record AddNoteShareRequest(Guid? UserId, Guid? RoomId, string Level);

/// Reactions are grouped by emoji: how many people used it, and whether the viewer is one of them.
public record NoteReactionDto(string Emoji, int Count, bool Mine);

public record NoteCommentDto(Guid Id, Guid? AuthorId, string AuthorName, string? AuthorNik, string Body, DateTime CreatedAt,
    DateTime? EditedAt = null, IReadOnlyList<NoteReactionDto>? Reactions = null, bool CanEdit = false);

public record NoteThreadDto(Guid Id, string? Quote, string? Anchor, DateTime CreatedAt, DateTime? ResolvedAt,
    IReadOnlyList<NoteCommentDto> Comments);

public record CreateNoteThreadRequest(string? Quote, string? Anchor, string Body, IReadOnlyList<Guid>? Mentions);

public record NoteReplyRequest(string Body, IReadOnlyList<Guid>? Mentions);

/// Result of posting a comment: the thread plus the mentioned users who can actually open the page
/// (the endpoint notifies these).
public record NoteCommentResult(NoteThreadDto Thread, IReadOnlyList<Guid> NotifyUserIds);

public record NoteSearchHitDto(Guid Id, string Title, string? Emoji, string? Snippet);

/// One row of the paged search. TitleExact/TitleMatch tell the caller how it matched (body otherwise).
public record NoteSearchRowDto(Guid Id, string Title, string? Emoji, string? Snippet, DateTime UpdatedAt, bool TitleExact, bool TitleMatch);

/// Truncated: at least one visible page's indexed text hit the PlainText cap, so a match beyond it would be missed.
public record NoteSearchPageDto(IReadOnlyList<NoteSearchRowDto> Items, int Total, bool Truncated);

/// Chat card payload, stored as "[note]" + json in ChatMessage.Content.
public record NoteImageDto(Guid Id, string FileName, long Size);

public record NoteCardDto(Guid Id, string Title, string? Emoji, string? Cover, string Accent, string Level, string? Excerpt,
    Guid? CoverImageId = null, int CoverPosY = 50);

/// A person in the Notes pickers. Nik is the photo/colour key only and is never shown (office rule,
/// same as the other DTOs); Department is the second line. HasAccess is set when the search is made
/// for a page (null otherwise).
public record NotePersonDto(Guid Id, string Nik, string Name, string? Position, string? Department = null, bool? HasAccess = null);

public record NoteAccessCheckRequest(IReadOnlyList<Guid> UserIds);

public interface INoteService
{
    Task<NoteAccess> GetAccessAsync(Guid userId, Guid pageId, CancellationToken ct);

    /// Creates the caller's tutorial page the first time (never again once it exists, even in Trash).
    Task<Guid> EnsureGuideAsync(Guid userId, CancellationToken ct);
    Task<NoteSidebarDto> GetSidebarAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(Guid userId, string query, CancellationToken ct);
    /// Title + indexed plain text of every page the user can open, ranked exact title › title › body, newest first.
    /// Only the stored PlainText is searched — never the binary Yjs state.
    Task<NoteSearchPageDto> SearchPagedAsync(Guid userId, string query, int page, int pageSize, CancellationToken ct);
    Task<IReadOnlyList<NoteListItemDto>> GetTrashAsync(Guid userId, CancellationToken ct);

    Task<NoteResult<NotePageDto>> GetPageAsync(Guid userId, Guid pageId, CancellationToken ct);
    Task<NoteResult<NotePageDto>> CreateAsync(Guid userId, CreateNoteRequest req, CancellationToken ct);
    Task<NoteResult<NotePageDto>> UpdateMetaAsync(Guid userId, Guid pageId, UpdateNoteMetaRequest req, CancellationToken ct);
    /// Lock or unlock a page. Needs edit access; a locked page refuses every change until it is unlocked.
    Task<NoteResult<NotePageDto>> SetLockedAsync(Guid userId, Guid pageId, bool locked, CancellationToken ct);
    Task<NoteResult<bool>> SetFavoriteAsync(Guid userId, Guid pageId, bool favorite, CancellationToken ct);
    /// Re-parent (owner only, target must be editable) and/or reorder among siblings (edit on the parent).
    Task<NoteResult<bool>> MoveAsync(Guid userId, Guid pageId, MoveNoteRequest req, CancellationToken ct);
    Task<NoteResult<NotePageDto>> DuplicateAsync(Guid userId, Guid pageId, CancellationToken ct);
    /// Creates the empty pages of an import in one go (tree included), private to the caller unless they
    /// land under a shared page. Limit: <see cref="NoteImport.MaxPages"/> pages.
    Task<NoteResult<ImportNotesResultDto>> ImportAsync(Guid userId, ImportNotesRequest req, CancellationToken ct);
    /// The caller's live pages imported earlier from any of <paramref name="importKeys"/>.
    Task<IReadOnlyList<PreviousImportDto>> FindImportedAsync(Guid userId, IReadOnlyList<string> importKeys, CancellationToken ct);
    Task<NoteResult<bool>> TrashAsync(Guid userId, Guid pageId, CancellationToken ct);
    Task<NoteResult<bool>> RestoreAsync(Guid userId, Guid pageId, CancellationToken ct);
    Task<NoteResult<bool>> PurgeAsync(Guid userId, Guid pageId, CancellationToken ct);

    Task<NoteResult<NoteDocDto>> GetDocAsync(Guid userId, Guid pageId, CancellationToken ct);
    Task<NoteResult<long>> AppendUpdateAsync(Guid userId, Guid pageId, byte[] update, CancellationToken ct);
    Task<NoteResult<NoteSnapshotResultDto>> SnapshotAsync(Guid userId, Guid pageId, NoteSnapshotRequest req, CancellationToken ct, DateTime? now = null);
    /// Atomically hands the page's seed template to exactly one caller (null if already claimed).
    Task<NoteResult<string?>> ClaimSeedAsync(Guid userId, Guid pageId, CancellationToken ct);

    Task<NoteResult<IReadOnlyList<NoteVersionDto>>> GetVersionsAsync(Guid userId, Guid pageId, CancellationToken ct);
    Task<NoteResult<string>> GetVersionStateAsync(Guid userId, Guid pageId, Guid versionId, CancellationToken ct);
    /// Gives a version a name (or clears it with an empty name). Needs edit access. Named versions are kept forever.
    Task<NoteResult<NoteVersionDto>> NameVersionAsync(Guid userId, Guid pageId, Guid versionId, string? name, CancellationToken ct);
    /// Saves the page's current content as a named version right now (the "Name this version" on the live page).
    Task<NoteResult<NoteVersionDto>> SaveNamedVersionAsync(Guid userId, Guid pageId, string? name, CancellationToken ct);

    Task<NoteResult<NoteTemplateDto>> SaveTemplateAsync(Guid userId, Guid pageId, string? name, CancellationToken ct);
    Task<IReadOnlyList<NoteTemplateDto>> GetTemplatesAsync(Guid userId, CancellationToken ct);
    Task<NoteResult<NotePageDto>> CreateFromTemplateAsync(Guid userId, Guid templateId, NewFromTemplateRequest req, CancellationToken ct);
    Task<NoteResult<bool>> DeleteTemplateAsync(Guid userId, Guid templateId, CancellationToken ct);

    /// The caller's own library items plus everything shared to the team library, newest first.
    Task<IReadOnlyList<NoteLibraryItemDto>> GetLibraryAsync(Guid userId, CancellationToken ct);
    Task<NoteResult<NoteLibraryItemDto>> SaveLibraryItemAsync(Guid userId, SaveLibraryItemRequest req, CancellationToken ct);
    Task<NoteResult<NoteLibraryItemDto>> UpdateLibraryItemAsync(Guid userId, Guid itemId, UpdateLibraryItemRequest req, CancellationToken ct);
    Task<NoteResult<bool>> DeleteLibraryItemAsync(Guid userId, Guid itemId, CancellationToken ct);

    /// Whiteboard templates: mine + every shared one (Team templates).
    Task<IReadOnlyList<NoteBoardTemplateDto>> GetBoardTemplatesAsync(Guid userId, CancellationToken ct);
    Task<NoteResult<NoteBoardTemplateDto>> SaveBoardTemplateAsync(Guid userId, SaveBoardTemplateRequest req, CancellationToken ct);
    Task<NoteResult<NoteBoardTemplateDto>> UpdateBoardTemplateAsync(Guid userId, Guid id, UpdateBoardTemplateRequest req, CancellationToken ct);
    /// The owner removes their own; `isAdmin` (notes.admin) may also remove a shared one.
    Task<NoteResult<bool>> DeleteBoardTemplateAsync(Guid userId, Guid id, bool isAdmin, CancellationToken ct);

    /// Owner and editors only: who opened the page, newest first.
    Task<NoteResult<IReadOnlyList<NoteViewerDto>>> GetViewersAsync(Guid userId, Guid pageId, CancellationToken ct);

    Task<NoteResult<NoteSharesDto>> GetSharesAsync(Guid userId, Guid pageId, CancellationToken ct);
    Task<NoteResult<NoteShareDto>> AddShareAsync(Guid userId, Guid pageId, AddNoteShareRequest req, CancellationToken ct);
    Task<NoteResult<bool>> UpdateShareAsync(Guid userId, Guid pageId, Guid shareId, string level, CancellationToken ct);
    Task<NoteResult<bool>> RemoveShareAsync(Guid userId, Guid pageId, Guid shareId, CancellationToken ct);
    Task<NoteResult<NoteCardDto>> GetCardAsync(Guid userId, Guid pageId, string level, CancellationToken ct);
    /// Users who can currently open the page (owners, direct shares, members of shared rooms) — for live refresh.
    Task<IReadOnlyList<Guid>> GetAudienceAsync(Guid pageId, CancellationToken ct);

    Task<NoteResult<IReadOnlyList<NoteThreadDto>>> GetThreadsAsync(Guid userId, Guid pageId, CancellationToken ct);
    Task<NoteResult<NoteCommentResult>> CreateThreadAsync(Guid userId, Guid pageId, CreateNoteThreadRequest req, CancellationToken ct);
    Task<NoteResult<NoteCommentResult>> ReplyAsync(Guid userId, Guid pageId, Guid threadId, NoteReplyRequest req, CancellationToken ct);
    /// Edit your own comment (not the built-in guide comment). Marks it edited.
    Task<NoteResult<NoteThreadDto>> EditCommentAsync(Guid userId, Guid pageId, Guid commentId, string body, CancellationToken ct);
    /// Delete a comment: its author, or the page owner. Deleting the last comment of a thread removes the thread.
    Task<NoteResult<NoteThreadDto?>> DeleteCommentAsync(Guid userId, Guid pageId, Guid commentId, CancellationToken ct);
    /// Toggle one emoji of the caller on a comment (anyone who can comment).
    Task<NoteResult<NoteThreadDto>> ToggleReactionAsync(Guid userId, Guid pageId, Guid commentId, string emoji, CancellationToken ct);
    /// System clean-up (no user involved): permanently removes every page that has been in Trash for longer than
    /// <paramref name="retention"/>, with its sub-pages, versions and comments, and returns the ids of the files that were
    /// attached to them so the caller can delete those from storage. Pages in Trash that belong to a still-live parent chain
    /// are handled like a manual purge (other people's sub-pages are lifted to the top level, never destroyed).
    Task<IReadOnlyList<Guid>> PurgeExpiredTrashAsync(TimeSpan retention, int maxPages, CancellationToken ct);
    /// Pages that link to this one and that the viewer can open (newest edit first, at most 30).
    Task<NoteResult<IReadOnlyList<NoteBacklinkDto>>> GetBacklinksAsync(Guid userId, Guid pageId, CancellationToken ct);
    Task<NoteResult<bool>> SetResolvedAsync(Guid userId, Guid pageId, Guid threadId, bool resolved, CancellationToken ct);
    /// The tutorial's example thread, posted by "iPop Guide" once the guide template is in place.
    Task<NoteResult<bool>> AddGuideThreadAsync(Guid userId, Guid pageId, string quote, string anchor, CancellationToken ct);

    Task<NoteResult<bool>> CanUploadAsync(Guid userId, Guid pageId, CancellationToken ct);
    Task LinkAttachmentAsync(Guid pageId, Guid attachmentId, CancellationToken ct);
    /// Makes an already-uploaded, page-linked image the page's cover (Can edit). Image-only; replaces a gradient.
    /// A repeating page's run: copies the template page as it is now into a new page next to it, owned by <paramref name="ownerId"/>
    /// (the person who set the repeat up), titled <paramref name="title"/>. Shares are not copied: the new page sits in the same parent,
    /// so it is open to the same people as the template. Returns the new page id and who can open it (to tell them).
    Task<NoteResult<NoteRecurrenceRunDto>> CreateRecurringCopyAsync(Guid templatePageId, Guid ownerId, string title, CancellationToken ct);
    Task<NoteResult<NoteRecurrenceDto?>> GetRecurrenceAsync(Guid userId, Guid pageId, CancellationToken ct);
    /// Sets (or changes) how a page repeats; needs edit access. The first page is made on the first matching day from today on.
    Task<NoteResult<NoteRecurrenceDto>> SetRecurrenceAsync(Guid userId, Guid pageId, SetNoteRecurrenceRequest req, DateOnly today, CancellationToken ct);
    Task<NoteResult<bool>> StopRecurrenceAsync(Guid userId, Guid pageId, CancellationToken ct);
    Task<NoteResult<NotePageDto>> SetCoverImageAsync(Guid userId, Guid pageId, Guid attachmentId, CancellationToken ct);
    /// Whiteboards: stores the picture the editor drew of the board as the page's thumbnail (replacing the previous one).
    /// Returns the attachment id that is no longer used so the caller can delete it from the store.
    Task<NoteResult<Guid?>> SetThumbnailAsync(Guid userId, Guid pageId, Guid attachmentId, CancellationToken ct);
    /// Images attached to this page (newest first), for "pick from this page".
    Task<NoteResult<IReadOnlyList<NoteImageDto>>> GetPageImagesAsync(Guid userId, Guid pageId, CancellationToken ct);

    /// People picker for sharing and @mentions (active users by name or email). With a page, each row
    /// says whether that person can open it.
    Task<IReadOnlyList<NotePersonDto>> SearchPeopleAsync(string query, CancellationToken ct, Guid? userId = null, Guid? pageId = null);
    /// Which of these people can open the page (the caller must be able to open it too).
    Task<NoteResult<IReadOnlyList<Guid>>> WhoCanAccessAsync(Guid userId, Guid pageId, IReadOnlyList<Guid> userIds, CancellationToken ct);
    /// An @mention typed into the page body: returns the mentioned users who can open the page.
    Task<NoteResult<IReadOnlyList<Guid>>> MentionInDocAsync(Guid userId, Guid pageId, IReadOnlyList<Guid> mentions, CancellationToken ct);
}
