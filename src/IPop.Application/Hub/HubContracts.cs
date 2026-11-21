namespace IPop.Application.Hub;

/// The Hub — confidential cross-department topics (docs/superpowers/specs/2026-11-21-the-hub-design.md).
/// Shared by the API and the Web client.
public static class HubKinds
{
    public const string Problem = "problem";
    public const string Clarity = "clarity";
    public const string Idea = "idea";
    public const string Change = "change";
    public const string Buy = "buy";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [Problem, Clarity, Idea, Change, Buy, Other];

    public static string Label(string kind) => kind switch
    {
        Problem => "Problem",
        Clarity => "Need clarity",
        Idea => "Idea",
        Change => "Product change",
        Buy => "Before you buy",
        _ => "Other",
    };
}

public static class HubStatus
{
    public const string New = "new";
    public const string Ongoing = "ongoing";
    public const string Solved = "solved";
}

public static class HubEvents
{
    /// (topicId) — to the topic's members: something changed, re-read the list / the open topic.
    public const string Changed = "hubChanged";
    public const string BadgeKey = "hub";
    public const string Permission = "hub.use";
    public const int TitleMax = 200;
    public const int NeedMax = 4000;
    public const int BodyMax = 8000;
    public const int SummaryMax = 2000;
}

public sealed record HubPersonDto(Guid Id, string Name, string? Department, string? PhotoUrl);

public sealed record HubTopicListItemDto(
    Guid Id, string Kind, string Title, string Status, DateTime CreatedAt, DateTime LastActivityAt,
    DateTime? DueDate, DateTime? SolvedAt, HubPersonDto CreatedBy, IReadOnlyList<HubPersonDto> People,
    int DepartmentCount, int Replies, bool Unread, bool WaitingForMe, bool MeLate,
    IReadOnlyList<string> WaitingFor, IReadOnlyList<string> LateFor, string? LastAuthor, string? LastPreview);

public sealed record HubCountsDto(int Mine, int New, int Ongoing, int Solved);

public sealed record HubListDto(IReadOnlyList<HubTopicListItemDto> Topics, HubCountsDto Counts);

public sealed record HubAskDto(Guid Id, Guid PostId, HubPersonDto To, HubPersonDto AskedBy, string Question,
    DateTime CreatedAt, DateTime? AnsweredAt, bool Late, DateTime? LastRemindedAt);

public sealed record HubFileDto(Guid Id, string FileName, string ContentType, long Size, string Url, Guid? PostId, string Uploader);

public sealed record HubPostDto(Guid Id, Guid? ParentId, HubPersonDto? Author, string Body, string Kind, DateTime CreatedAt,
    bool IsNew, IReadOnlyList<HubFileDto> Files, Guid? AskTo, string? AskToName, bool AskAnswered,
    DateTime? EditedAt = null, bool Deleted = false, IReadOnlyList<string>? NotedBy = null, bool NotedByMe = false, bool Best = false);

/// Who in the topic has read it up to the latest post ("Seen by 3 of 4"), and who hasn't yet.
public sealed record HubSeenDto(int Seen, int Total, IReadOnlyList<string> NotYet);

public sealed record HubTaskLinkDto(Guid TaskId, Guid ProjectId, string Title, string ProjectName, string Assignee, DateTime? DueDate);

public sealed record HubTopicDto(
    Guid Id, string Kind, string Title, string Need, string Status, DateTime CreatedAt, DateTime? DueDate,
    HubPersonDto CreatedBy, IReadOnlyList<HubPersonDto> People, IReadOnlyList<string> Departments,
    IReadOnlyList<HubPostDto> Posts, IReadOnlyList<HubAskDto> Asks, IReadOnlyList<HubFileDto> Files,
    DateTime? SolvedAt, HubPersonDto? SolvedBy, string? SolvedSummary, IReadOnlyList<HubTaskLinkDto> Tasks,
    DateTime? ReadUpTo, Guid? BestPostId = null, HubSeenDto? Seen = null);

public sealed record CreateHubTopicRequest(string? Kind, string? Title, string? Need, DateTime? DueDate, IReadOnlyList<Guid>? People);
public sealed record CreateHubPostRequest(string? Body, Guid? ParentId, Guid? AskUserId, IReadOnlyList<Guid>? AttachmentIds);
public sealed record EditHubPostRequest(string? Body);
public sealed record HubBestRequest(Guid? PostId);
public sealed record AddHubMembersRequest(IReadOnlyList<Guid>? People);
public sealed record HubFollowUpTask(string? Title, Guid AssigneeId, Guid ProjectId, DateTime? DueDate);
public sealed record SolveHubTopicRequest(string? Summary, IReadOnlyList<HubFollowUpTask>? Tasks);
public sealed record HubProjectOptionDto(Guid Id, string Name, string? Color);
public sealed record HubUploadDto(Guid Id, string FileName, string ContentType, long Size, string Url);

/// What a Hub call came back with: Success, or an error code ("not_found", "invalid", "rule", "rate_limited", "forbidden").
public sealed record HubResult<T>(bool Success, T? Value, string? Error, string? Message = null)
{
    public static HubResult<T> Ok(T value) => new(true, value, null);
    public static HubResult<T> Fail(string code, string message) => new(false, default, code, message);
}

public interface IHubService
{
    Task<HubListDto> ListAsync(Guid me, string? filter, string? q, CancellationToken ct);
    Task<HubResult<HubTopicDto>> GetAsync(Guid me, Guid topicId, bool markRead, CancellationToken ct);
    Task<HubResult<HubTopicDto>> CreateAsync(Guid me, CreateHubTopicRequest req, CancellationToken ct);
    Task<HubResult<HubTopicDto>> PostAsync(Guid me, Guid topicId, CreateHubPostRequest req, CancellationToken ct);
    Task<HubResult<HubTopicDto>> EditPostAsync(Guid me, Guid topicId, Guid postId, EditHubPostRequest req, CancellationToken ct);
    Task<HubResult<HubTopicDto>> DeletePostAsync(Guid me, Guid topicId, Guid postId, CancellationToken ct);
    /// Toggles my "Noted" on a reply.
    Task<HubResult<HubTopicDto>> NoteAsync(Guid me, Guid topicId, Guid postId, CancellationToken ct);
    /// Marks a reply as the best answer, or clears it when postId is null or already the best one.
    Task<HubResult<HubTopicDto>> BestAsync(Guid me, Guid topicId, Guid? postId, CancellationToken ct);
    Task<HubResult<HubTopicDto>> AddMembersAsync(Guid me, Guid topicId, AddHubMembersRequest req, CancellationToken ct);
    Task<HubResult<bool>> RemindAsync(Guid me, Guid topicId, Guid askId, CancellationToken ct);
    Task<HubResult<HubTopicDto>> SolveAsync(Guid me, Guid topicId, SolveHubTopicRequest req, CancellationToken ct);
    Task<HubResult<HubTopicDto>> ReopenAsync(Guid me, Guid topicId, CancellationToken ct);
    Task<bool> IsMemberAsync(Guid me, Guid topicId, CancellationToken ct);
    Task<HubResult<bool>> LinkUploadAsync(Guid me, Guid topicId, Guid attachmentId, CancellationToken ct);
    Task<int> WaitingCountAsync(Guid me, CancellationToken ct);
}
