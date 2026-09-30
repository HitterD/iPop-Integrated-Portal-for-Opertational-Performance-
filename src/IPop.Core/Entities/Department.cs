namespace IPop.Core.Entities;

public class Department
{
    public Guid Id { get; set; }
    public required string Code { get; set; }        // unique — from HRIS kodeDept
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }              // self-reference for sub-departments
    public Guid? SiteId { get; set; }                // home site — places a top-level unit in the Directory tree even when it has no divisions yet
    public int SortOrder { get; set; }               // sibling order in the Directory tree (0 = alphabetical)
    public bool IsActive { get; set; } = true;
    public Guid? HeadUserId { get; set; }            // unit head — first level of the auto org chart

    public Department? Parent { get; set; }
    public Site? Site { get; set; }
    public User? Head { get; set; }
    public ICollection<User> Members { get; set; } = [];
}
