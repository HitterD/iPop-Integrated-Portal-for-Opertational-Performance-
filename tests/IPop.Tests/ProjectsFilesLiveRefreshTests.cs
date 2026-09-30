using System.Text.RegularExpressions;
using Xunit;

namespace IPop.Tests;

/// Source contract for live refresh: every mutating Projects route pushes projectChanged and
/// every mutating Files route pushes filesChanged. A new route that forgets the push leaves
/// other users' pages stale with no failing behaviour anywhere else, so pin it here.
public class ProjectsFilesLiveRefreshTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IPop.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    /// (verb, route, body) for each g.MapXxx("route", ...) registration.
    private static IEnumerable<(string Verb, string Route, string Body)> Routes(string source)
    {
        var parts = Regex.Split(source, @"(?=\n\s*g\.Map)");
        foreach (var p in parts.Skip(1))
        {
            var m = Regex.Match(p, @"^\s*g\.Map(\w+)\(""([^""]*)""");
            if (m.Success) yield return (m.Groups[1].Value, m.Groups[2].Value, p);
        }
    }

    [Fact]
    public void EveryMutatingProjectRoute_PushesProjectChanged()
    {
        var src = RepoFile("src/IPop.Api/Endpoints/ProjectEndpoints.cs");
        // Templates are not tied to a project; nothing on screen shows them live.
        var exempt = new HashSet<string> { "Post /templates" };
        var missing = Routes(src)
            .Where(r => r.Verb != "Get" && !exempt.Contains($"{r.Verb} {r.Route}"))
            .Where(r => !r.Body.Contains("PushProjectChangedAsync("))
            .Select(r => $"{r.Verb} {r.Route}")
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void ProjectRoutes_AreNotRegisteredTwice()
    {
        var src = RepoFile("src/IPop.Api/Endpoints/ProjectEndpoints.cs");
        var dupes = Routes(src).GroupBy(r => $"{r.Verb} {r.Route}").Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes); // an identical second mapping throws AmbiguousMatchException per call
    }

    [Theory]
    [InlineData("Post", "/upload")]
    [InlineData("Post", "/rename")]
    [InlineData("Post", "/folder")]
    [InlineData("Post", "/delete")]
    [InlineData("Post", "/move")]
    [InlineData("Post", "/copy")]
    [InlineData("Post", "/recycle/restore")]
    [InlineData("Post", "/recycle/empty")]
    [InlineData("Post", "/sharing")]
    [InlineData("Delete", "/sharing")]
    public void MutatingFileRoute_PushesFilesChanged(string verb, string route)
    {
        var src = RepoFile("src/IPop.Api/Endpoints/FileEndpoints.cs");
        var r = Routes(src).Single(x => x.Verb == verb && x.Route == route);
        Assert.Contains("FilesRealtime.PushAsync(", r.Body);
    }
}
