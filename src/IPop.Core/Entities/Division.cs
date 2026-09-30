namespace IPop.Core.Entities;

/// A Division bundles one parent Department with one Site — the admin-owned lookup
/// that backs the Directory tree (Site ▸ Department ▸ Division). HRIS never touches it.
/// A user assigned to a Division keeps their Site/Department in sync from its parents.
public class Division
{
    public Guid Id { get; set; }
    public required string Code { get; set; }        // unique
    public required string Name { get; set; }
    public Guid? DepartmentId { get; set; }           // parent department (one side of the bundle)
    public Guid? SiteId { get; set; }                // parent site (the other side)
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }               // sibling order in the Directory tree (0 = alphabetical)
    public Guid? HeadUserId { get; set; }            // unit head — drives the auto org chart

    public Department? Department { get; set; }
    public Site? Site { get; set; }
    public User? Head { get; set; }
}
