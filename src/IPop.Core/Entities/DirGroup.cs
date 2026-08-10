namespace IPop.Core.Entities;

/// A custom folder in the directory tree. Admin-owned grouping — never touched by HRIS.
public class DirGroup
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }          // self-reference for nested folders
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public DirGroup? Parent { get; set; }
    public ICollection<UserDirGroup> Members { get; set; } = [];
}
