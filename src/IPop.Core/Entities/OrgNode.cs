namespace IPop.Core.Entities;

/// A position in the org chart. Admin-owned — built from scratch, never touched by HRIS.
public class OrgNode
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Title { get; set; }           // custom position shown on the chart card
    public Guid? ParentId { get; set; }          // self-reference for reporting lines
    public Guid? DepartmentId { get; set; }      // optional; groups nodes under a department header
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public OrgNode? Parent { get; set; }
    public Department? Department { get; set; }
    public ICollection<UserOrgNode> Members { get; set; } = [];
}
