using System.Text.RegularExpressions;
using Xunit;

namespace IPop.Tests;

/// /api/admin/org is open to directory.manage (HC presets) — but the tree-wide destructive actions
/// (purge every department, publish the whole org chart) stay Admin-only. Source-level check, same
/// style as NotifierLinkTests: the route table needs the full API container to build.
public class AdminOrgAuthorizationTests
{
    private static string Source(string file) =>
        File.ReadAllText(Path.Combine(NotifierLinkTests.RepoRoot(), "src", "IPop.Api", "Endpoints", file));

    /// The statement that maps <paramref name="route"/>: from the Map call to the end of its chain.
    private static string Mapping(string file, string route)
    {
        var src = Source(file);
        var start = src.IndexOf($"\"{route}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{route} not found in {file}");
        var end = src.IndexOf("\n\n", start, StringComparison.Ordinal);
        return src[start..(end < 0 ? src.Length : end)];
    }

    [Theory]
    [InlineData("AdminEndpoints.cs", "/departments/purge")]
    [InlineData("OrgChartEndpoints.cs", "/chart/publish")]
    public void DestructiveOrgActions_StayAdminOnly(string file, string route) =>
        Assert.Contains(".RequireAuthorization(\"AdminOnly\")", Mapping(file, route));

    [Fact]
    public void OrgGroup_IsOpenToDirectoryManage()
    {
        var src = Source("AdminEndpoints.cs");
        Assert.Matches(new Regex(@"MapGroup\(""/api/admin/org""\)\.RequireAuthorization\(\)\.RequirePermission\(ApprovalEndpoints\.OrgPermission\)"), src);
    }
}
