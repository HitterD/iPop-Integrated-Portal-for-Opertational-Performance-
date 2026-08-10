namespace IPop.Core.Entities;

/// A physical site (office, plant, warehouse). Admin-owned — HRIS never touches it.
/// A user belongs to at most one site (optional); departments are independent of sites.
public class Site
{
    public Guid Id { get; set; }
    public required string Code { get; set; }     // unique
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
}
