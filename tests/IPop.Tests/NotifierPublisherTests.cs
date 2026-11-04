using System.Text.Json.Nodes;
using IPop.Api.Notifier;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Cluster-side publishing (GitHub Release → pilot → everyone) writes the same folder layout as publish-notifier.ps1.
public class NotifierPublisherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ipop-publisher-" + Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(root, "releases");
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);

    public NotifierPublisherTests() => Directory.CreateDirectory(Folder);
    public void Dispose() { try { Directory.Delete(root, true); } catch { } }

    private (NotifierPublisher Pub, NotifierReleases Rel) Make(string? packId = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifier:ReleasesPath"] = Folder,
            ["Notifier:PackId"] = packId,
        }).Build();
        var rel = new NotifierReleases(cfg);
        return (new NotifierPublisher(rel, cfg), rel);
    }

    /// A vpk-like build output: full package (+ delta when a previous version is given), feed, side files, msi.
    private string Build(string version, string? previous = null, bool msi = true, string packId = "iPopNotifier")
    {
        var dir = Path.Combine(root, "build-" + version + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var assets = new JsonArray();
        void Pkg(string v, string type, bool write)
        {
            var name = $"{packId}-{v}-{type.ToLowerInvariant()}.nupkg";
            var path = Path.Combine(dir, name);
            if (write) File.WriteAllText(path, $"{packId} {v} {type}");
            var sha = write ? NotifierPublisher.Hash(path) : null;
            assets.Add(new JsonObject { ["PackageId"] = packId, ["Version"] = v, ["Type"] = type, ["FileName"] = name, ["SHA256"] = sha });
        }
        if (previous is not null) Pkg(previous, "Full", write: false);   // already in the folder
        Pkg(version, "Full", true);
        if (previous is not null) Pkg(version, "Delta", true);
        File.WriteAllText(Path.Combine(dir, "releases.win.json"), new JsonObject { ["Assets"] = assets }.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "RELEASES"), "x " + version);
        File.WriteAllText(Path.Combine(dir, "assets.win.json"), "[]");
        File.WriteAllText(Path.Combine(dir, $"{packId}-win-Setup.exe"), "setup " + version);
        if (msi) File.WriteAllText(Path.Combine(dir, $"{packId}-win-GPO.msi"), "msi " + version);
        return dir;
    }

    [Fact]
    public async Task Ingest_makes_a_pilot_and_keeps_everyone_on_the_old_release()
    {
        var (pub, rel) = Make();
        await pub.IngestAsync(Build("1.5.0"), "1.5.0", false, Now);
        await pub.PromoteAsync(Now);
        await pub.IngestAsync(Build("1.5.1", previous: "1.5.0"), "1.5.1", mandatory: true, Now);

        var state = rel.Read();
        Assert.Equal("1.5.0", state.All?.Version);
        Assert.Equal("1.5.1", state.Pilot?.Version);
        Assert.True(state.Pilot?.Mandatory);
        Assert.Equal("setup 1.5.0", File.ReadAllText(Path.Combine(Folder, "iPopNotifier-win-Setup.exe")));
        Assert.Equal("msi 1.5.0", File.ReadAllText(Path.Combine(Folder, "iPopNotifier-win-GPO.msi")));
        var (allFeed, _) = rel.FeedFor(pilot: false);
        var (pilotFeed, target) = rel.FeedFor(pilot: true);
        Assert.DoesNotContain("1.5.1", allFeed);
        Assert.Contains("1.5.1", pilotFeed);
        Assert.Equal("1.5.1", target?.Version);
        Assert.True(File.Exists(Path.Combine(Folder, "pilot", "1.5.1", "receipt.json")));
    }

    [Fact]
    public async Task Promote_publishes_installer_msi_and_feed_then_clears_the_pilot()
    {
        var (pub, rel) = Make();
        await pub.IngestAsync(Build("1.5.0"), "1.5.0", false, Now);
        await pub.PromoteAsync(Now);
        await pub.IngestAsync(Build("1.5.1", previous: "1.5.0"), "1.5.1", false, Now);
        var outcome = await pub.PromoteAsync(Now.AddHours(1));

        Assert.Equal("1.5.1", outcome.Version);
        var state = rel.Read();
        Assert.Equal("1.5.1", state.All?.Version);
        Assert.Null(state.Pilot);
        Assert.Equal("setup 1.5.1", File.ReadAllText(Path.Combine(Folder, "iPopNotifier-win-Setup.exe")));
        Assert.Equal("msi 1.5.1", File.ReadAllText(Path.Combine(Folder, "iPopNotifier-win-GPO.msi")));
        Assert.Contains("1.5.1", rel.FeedFor(false).FeedJson);
        Assert.True(File.Exists(Path.Combine(Folder, "releases.win.json.bak")));
        await Assert.ThrowsAsync<NotifierPublisher.PublishException>(() => pub.PromoteAsync(Now));
    }

    [Fact]
    public async Task Same_or_older_version_is_refused()
    {
        var (pub, _) = Make();
        await pub.IngestAsync(Build("1.5.0"), "1.5.0", false, Now);
        await Assert.ThrowsAsync<NotifierPublisher.PublishException>(() => pub.IngestAsync(Build("1.5.0"), "1.5.0", false, Now));
        await Assert.ThrowsAsync<NotifierPublisher.PublishException>(() => pub.IngestAsync(Build("1.4.9"), "1.4.9", false, Now));
    }

    [Fact]
    public async Task Tampered_pilot_is_not_released()
    {
        var (pub, rel) = Make();
        await pub.IngestAsync(Build("1.5.0"), "1.5.0", false, Now);
        File.WriteAllText(Path.Combine(Folder, "pilot", "1.5.0", "iPopNotifier-win-Setup.exe"), "evil");
        await Assert.ThrowsAsync<NotifierPublisher.PublishException>(() => pub.PromoteAsync(Now));
        Assert.Equal("1.5.0", rel.Read().Pilot?.Version);
        Assert.False(File.Exists(Path.Combine(Folder, "iPopNotifier-win-Setup.exe")));
    }

    [Fact]
    public async Task Package_not_matching_its_feed_hash_is_refused()
    {
        var (pub, rel) = Make();
        var build = Build("1.5.0");
        File.WriteAllText(Path.Combine(build, "iPopNotifier-1.5.0-full.nupkg"), "corrupted download");
        await Assert.ThrowsAsync<NotifierPublisher.PublishException>(() => pub.IngestAsync(build, "1.5.0", false, Now));
        Assert.Null(rel.Read().Pilot);
    }

    [Fact]
    public async Task Wrong_package_identity_is_refused()
    {
        var (pub, _) = Make(packId: "iPopNotifierQA");
        await Assert.ThrowsAsync<NotifierPublisher.PublishException>(() => pub.IngestAsync(Build("1.5.0"), "1.5.0", false, Now));
    }

    [Fact]
    public async Task Interrupted_ingest_of_the_same_version_can_be_retried()
    {
        var (pub, rel) = Make();
        // A crash left the held folder and the package, but release.json never named the pilot.
        Directory.CreateDirectory(Path.Combine(Folder, "pilot", "1.5.0"));
        var build = Build("1.5.0");
        File.Copy(Path.Combine(build, "iPopNotifier-1.5.0-full.nupkg"), Path.Combine(Folder, "iPopNotifier-1.5.0-full.nupkg"));
        await pub.IngestAsync(build, "1.5.0", false, Now);
        Assert.Equal("1.5.0", rel.Read().Pilot?.Version);
    }

    [Fact]
    public async Task Build_without_msi_still_releases()
    {
        var (pub, rel) = Make();
        await pub.IngestAsync(Build("1.5.0", msi: false), "1.5.0", false, Now);
        await pub.PromoteAsync(Now);
        Assert.Equal("1.5.0", rel.Read().All?.Version);
        Assert.False(File.Exists(Path.Combine(Folder, "iPopNotifier-win-GPO.msi")));
    }

    [Fact]
    public async Task Existing_script_published_folder_is_continued()
    {
        // As publish-notifier.ps1 left it on the Windows host: release.json + public feed, no pilot.
        var (pub, rel) = Make();
        File.WriteAllText(Path.Combine(Folder, "iPopNotifier-1.4.7-full.nupkg"), "old");
        File.WriteAllText(Path.Combine(Folder, NotifierReleases.AllFeed),
            """{"Assets":[{"PackageId":"iPopNotifier","Version":"1.4.7","Type":"Full","FileName":"iPopNotifier-1.4.7-full.nupkg"}]}""");
        File.WriteAllText(Path.Combine(Folder, NotifierReleases.Manifest), """{"all":{"version":"1.4.7","mandatory":false},"pilot":null}""");
        await Assert.ThrowsAsync<NotifierPublisher.PublishException>(() => pub.IngestAsync(Build("1.4.7"), "1.4.7", false, Now));
        await pub.IngestAsync(Build("1.4.8", previous: "1.4.7"), "1.4.8", false, Now);
        await pub.PromoteAsync(Now);
        Assert.Equal("1.4.8", rel.Read().All?.Version);
        Assert.Contains("1.4.7-full", rel.FeedFor(false).FeedJson);   // still there for deltas
    }

    [Fact]
    public void Github_release_pick_takes_the_newest_published_tag_with_the_prefix()
    {
        var list = JsonNode.Parse("""
            [
              {"tag_name":"notifier-v1.5.2","draft":true,"assets":[]},
              {"tag_name":"notifier-v1.5.1","draft":false,"body":"Fixes\n\n[mandatory]","published_at":"2026-11-04T01:00:00Z","assets":[{"name":"releases.win.json","id":7}]},
              {"tag_name":"notifier-v1.10.0-beta","draft":false,"assets":[]},
              {"tag_name":"notifier-qa-v9.0.0","draft":false,"assets":[]},
              {"tag_name":"notifier-v1.4.9","draft":false,"assets":[]},
              {"tag_name":"main-abc","draft":false,"assets":[]}
            ]
            """);
        var pick = NotifierGithubSync.Pick(list, "notifier-v");
        Assert.Equal("1.5.1", pick?.Version);
        Assert.True(pick?.Mandatory);
        Assert.Equal(7, pick?.Assets["releases.win.json"]);
        Assert.Equal("9.0.0", NotifierGithubSync.Pick(list, "notifier-qa-v")?.Version);
        Assert.Null(NotifierGithubSync.Pick(JsonNode.Parse("[]"), "notifier-v"));
    }
}

public class NotifierGithubSyncTests
{
    [Fact]
    public async Task Missing_token_is_reported_but_not_a_failure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipop-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Notifier:ReleasesPath"] = dir }).Build();
            var services = new ServiceCollection().AddLogging().AddHttpClient().BuildServiceProvider();
            var rel = new NotifierReleases(cfg);
            var sync = new NotifierGithubSync(new NotifierPublisher(rel, cfg), rel, cfg, services.GetRequiredService<IHttpClientFactory>(),
                services.GetRequiredService<IServiceScopeFactory>(), NullLogger<NotifierGithubSync>.Instance);
            Assert.Equal(0, await sync.RunAsync(DateTime.UtcNow, CancellationToken.None));
            var status = sync.ReadStatus();
            Assert.Equal(NotifierGithubSync.NotConfigured, status?.LastError);
            Assert.Equal(0, status?.Failures);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
