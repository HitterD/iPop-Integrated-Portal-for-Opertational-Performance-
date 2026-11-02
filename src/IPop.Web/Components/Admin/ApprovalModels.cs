namespace IPop.Web.Components.Admin;

// Client shapes of /api/approval responses (mirror IPop.Application.Approval DTOs).
public record ApvStep(int Slot, string Label, string Nik, string? Name, bool Active, bool Known);
public record ApvChain(string Nik, string? Name, string? Plant, string? Division, string? Department,
    string?[] Slots, List<ApvStep> Steps, List<string> Issues, DateTime? UpdatedAt, string? UpdatedByName);
public record ApvPage(List<ApvChain> Items, int TotalCount, int Page, int PageSize);
public record ApvHistory(DateTime At, string? ActorName, string Summary);
public record ApvCountResult(int Updated);

public record ApvUnitCreate(string Kind, string Code, string Name, string? ParentCode, string? SiteCode);
public record ApvMove(string Nik, string Name, string? FromPath, string ToPath, string? SiteCode, string DepartmentCode, string? DivisionCode);
public record ApvDeactivate(string Kind, string Code, string Path);
public record ApvChange(string Nik, string Name, string Kind, string?[]? Before, string?[] After);
public record ApvPreview(string Token, int Unchanged, int SkippedResigned, int New, int Changed, int Cleared,
    List<ApvChange> Chains, List<ApvUnitCreate> UnitsToCreate, List<ApvMove> Moves, List<ApvDeactivate> UnitsToDeactivate,
    List<string> Unmapped, List<string> UnknownEmployees, List<string> InactiveApprovers, List<string> UnknownApprovers,
    List<string> Warnings)
{
    public bool HasChanges => Chains.Count > 0 || UnitsToCreate.Count > 0 || Moves.Count > 0 || UnitsToDeactivate.Count > 0;
}
public record ApvImportResult(int ChainsWritten, int UsersPlaced, int UnitsCreated, int UnitsDeactivated);

public static class ApvText
{
    public static readonly string[] SlotLabels =
    [
        "Manager", "Asst. Manager 1", "Asst. Manager 2", "Asst. Manager 3", "Asst. Manager 4",
        "Plant Manager", "Plant Ops Manager", "General Manager", "Director",
    ];

    public static (string Key, string Label)[] IssueFilters =
    [
        ("", "All"), ("inactive_approver", "Approver left"), ("unknown_approver", "No iPop account"),
        ("empty_chain", "No approvers"), ("not_in_matrix", "Not in workbook"),
    ];

    public static string Issue(string code) => code switch
    {
        "inactive_approver" => "An approver has left",
        "unknown_approver" => "An approver has no iPop account",
        "empty_chain" => "No approvers",
        "not_in_matrix" => "Not in the workbook",
        _ => code,
    };

    public static string ChipTitle(ApvStep s) => !s.Known ? "No iPop account" : !s.Active ? "No longer active" : s.Label;
}
