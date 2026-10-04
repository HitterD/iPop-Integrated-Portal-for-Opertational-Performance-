namespace IPop.Web.Services;

/// Organization occurrences are scoped by site, never by a display label.
public sealed record DirectoryNavNode(string Key, string Kind, string Code, string SiteCode,
    string Name, int Count, int Depth, string? ParentKey);

public sealed class DirectoryNavigation
{
    public List<DirectoryNavNode> Nodes { get; } = [];
    public HashSet<string> Expanded { get; set; } = [];
    public string? SelectedKey { get; set; }
    public bool ShowEmpty { get; set; }
    public string Search { get; set; } = "";
    public DirectoryNavNode? Selected => Nodes.FirstOrDefault(n => n.Key == SelectedKey);
    public void Load(DirectoryStructureNodeDto structure)
    {
        Nodes.Clear();
        foreach (var site in structure.Sites)
        {
            var key = $"site/{site.Code}";
            Nodes.Add(new(key, "site", site.Code, site.Code, site.Name, site.MemberCount, 0, null));
            foreach (var dept in site.Departments) AddDept(site.Code, dept, key, 1);
        }
        Expanded.IntersectWith(Nodes.Select(n => n.Key));
        if (Selected is null) SelectedKey = null;
    }
    private void AddDept(string site, DirectoryDepartmentNodeDto dept, string parent, int depth)
    {
        var key = $"{parent}/dept/{dept.Code}";
        Nodes.Add(new(key, "dept", dept.Code, site, dept.Name, dept.MemberCount, depth, parent));
        foreach (var div in dept.Divisions)
            Nodes.Add(new($"{key}/div/{div.Code}", "div", div.Code, site, div.Name, div.MemberCount, depth + 1, key));
        foreach (var child in dept.Children ?? []) AddDept(site, child, key, depth + 1);
    }
    public bool HasChildren(DirectoryNavNode node) => Nodes.Any(n => n.ParentKey == node.Key && (ShowEmpty || n.Count > 0));
    public IEnumerable<DirectoryNavNode> Path(DirectoryNavNode? node)
    {
        var path = new List<DirectoryNavNode>();
        while (node is not null) { path.Add(node); node = Nodes.FirstOrDefault(n => n.Key == node.ParentKey); }
        path.Reverse(); return path;
    }
    private HashSet<string> SearchPaths() => Nodes.Where(n => (ShowEmpty || n.Count > 0) && n.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))
        .SelectMany(n => Path(n)).Select(n => n.Key).ToHashSet();
    public bool IsOpen(DirectoryNavNode node) => !string.IsNullOrWhiteSpace(Search) ? SearchPaths().Contains(node.Key) : Expanded.Contains(node.Key);
    public IEnumerable<DirectoryNavNode> Visible()
    {
        var searching = !string.IsNullOrWhiteSpace(Search);
        var matches = searching ? SearchPaths() : null;
        return Nodes.Where(n => (ShowEmpty || n.Count > 0) && (searching ? matches!.Contains(n.Key) : Path(n).SkipLast(1).All(p => Expanded.Contains(p.Key))));
    }
}
