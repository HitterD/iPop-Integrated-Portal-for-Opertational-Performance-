namespace IPop.Core.Entities;

/// A box in the org chart: a position with a reporting line, held by zero or more people.
/// Admin-owned, edited as a draft in the browser and written in one go on Publish.
/// Replaces the retired OrgNode/UserOrgNode tables.
public class OrgPosition
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public Guid? ParentId { get; set; }          // reports to; null = top of the chart
    public Guid? DepartmentId { get; set; }      // colour/grouping unit (UI "Division")
    public Guid? SiteId { get; set; }            // plant; null = shown on every plant (e.g. the Board)
    public int SortOrder { get; set; }           // order among siblings

    public OrgPosition? Parent { get; set; }
    public Department? Department { get; set; }
    public Site? Site { get; set; }
    public ICollection<OrgPositionHolder> Holders { get; set; } = [];
}

/// A person in a position. One person may hold several positions (concurrent role);
/// the holder row with the lowest SortOrder across their positions is their primary one.
public class OrgPositionHolder
{
    public Guid PositionId { get; set; }
    public Guid UserId { get; set; }
    public int SortOrder { get; set; }

    public OrgPosition Position { get; set; } = null!;
    public User User { get; set; } = null!;
}

/// One row per Publish: the chart version (Id) for conflict checks, and who published when.
public class OrgChartPublication
{
    public int Id { get; set; }
    public DateTime PublishedAt { get; set; }
    public string? PublishedByNik { get; set; }
    public int PositionCount { get; set; }
}
