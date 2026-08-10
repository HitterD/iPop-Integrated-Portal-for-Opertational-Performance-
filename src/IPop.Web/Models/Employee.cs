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
    string? PhotoPath = null)
{
    public string Initials =>
        string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(w => w[0]).Take(2)).ToUpperInvariant();

    public string StatusLabel => Status switch
    {
        "on" => "Online",
        "away" => "Away",
        "busy" => "Busy",
        _ => "Offline"
    };
}

public enum DirectoryConnectionState
{
    Offline,
    Connecting,
    Live,
    Reconnecting
}
