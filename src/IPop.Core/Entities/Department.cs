namespace IPop.Core.Entities;

public class Department
{
    public Guid Id { get; set; }
    public required string Code { get; set; }        // unique — from HRIS kodeDept
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }              // self-reference for sub-departments
    public bool IsActive { get; set; } = true;

    public Department? Parent { get; set; }
    public ICollection<User> Members { get; set; } = [];
}
