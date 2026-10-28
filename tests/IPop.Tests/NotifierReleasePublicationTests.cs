using IPop.Api.Notifier;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class NotifierReleasePublicationTests
{
    [Fact]
    public void Feed_never_exposes_packages_newer_than_committed_manifest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipop-publish-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, NotifierReleases.Manifest), """
                {"all":{"version":"1.3.0","mandatory":true},"pilot":null}
                """);
            // Publisher has replaced the feed, but has not yet committed release.json.
            File.WriteAllText(Path.Combine(dir, NotifierReleases.AllFeed), """
                {"Assets":[{"Version":"1.3.0","Type":"Full"},{"Version":"1.3.1","Type":"Full"},{"Version":"1.3.1","Type":"Delta"}]}
                """);
            var releases = new NotifierReleases(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Notifier:ReleasesPath"] = dir }).Build());
            var (feed, target) = releases.FeedFor(false);
            Assert.Contains("1.3.0", feed);
            Assert.DoesNotContain("1.3.1", feed);
            Assert.Equal("1.3.0", target?.Version);
            Assert.True(target?.Mandatory);
        }
        finally { Directory.Delete(dir, true); }
    }
}
