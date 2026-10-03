namespace IPop.Web.Models;

public record Employee(
    string Name,
    string Position,
    string Nik,
    string Extension,
    string Location,
    string Department,
    string? DepartmentCode,
    string Site,
    string? SiteCode,
    string Manager,
    string Email,
    string Status, // "on" | "away" | "busy" | "off"
    string Color,
    string? PhotoPath = null,
    string? ManagerNik = null,
    string? Section = null,
    DateTime? LastSeenAt = null)
{
    /// "Department › Section" for the Directory list; "—" when neither is known.
    public string Placement => string.IsNullOrEmpty(Section) ? Department : $"{Department} › {Section}";
    public bool HasExtension => !string.IsNullOrWhiteSpace(Extension) && Extension != "—";

    public string Initials =>
        string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(w => w[0]).Take(2)).ToUpperInvariant();

    public string StatusLabel => Status switch
    {
        "on" => "Online",
        "away" => "Away",
        "busy" => "Busy",
        _ => LastSeen.OfflineLabel(LastSeenAt)
    };
}

public enum DirectoryConnectionState
{
    Offline,
    Connecting,
    Live,
    Reconnecting
}
