namespace IPop.Application.Approval;

/// Slot labels, in workbook order (slot 1 = index 0).
public static class ApproverSlots
{
    public static readonly string[] Labels =
    [
        "Manager", "Asst. Manager 1", "Asst. Manager 2", "Asst. Manager 3", "Asst. Manager 4",
        "Plant Manager", "Plant Ops Manager", "General Manager", "Director",
    ];
}

/// Issue codes surfaced to HC.
public static class ChainIssues
{
    public const string InactiveApprover = "inactive_approver";
    public const string UnknownApprover = "unknown_approver";
    public const string EmptyChain = "empty_chain";
    public const string NotInMatrix = "not_in_matrix";
}

/// One visible step of a compacted chain. Known = an iPop account with this NIK exists.
public record ChainStep(int Slot, string Label, string Nik, string? Name, bool Active, bool Known);

public record ChainView(string Nik, string? Name, string? Plant, string? Division, string? Department,
    string?[] Slots, IReadOnlyList<ChainStep> Steps, IReadOnlyList<string> Issues,
    DateTime? UpdatedAt, string? UpdatedByName);

/// Own chain for the profile: names and labels only, never NIKs.
public record MyApproverStep(string Label, string Name, bool Active);

public record ChainQuery(string? Search, string? SiteCode, string? DepartmentCode, string? DivisionCode,
    string? Issue, int Page, int PageSize);

public record ChainPage(IReadOnlyList<ChainView> Items, int TotalCount, int Page, int PageSize);

public record ChainHistoryEntry(DateTime At, string? ActorName, string Summary);
