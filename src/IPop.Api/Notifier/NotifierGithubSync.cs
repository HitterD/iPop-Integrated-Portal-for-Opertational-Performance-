using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using IPop.Application.Notifications;

namespace IPop.Api.Notifier;

/// The cluster's "notifier-sync" CronJob (API image, --notifier-sync, every 5 minutes). GitHub can't reach the LAN,
/// so the cluster pulls: the newest published (non-draft) GitHub Release whose tag is Notifier:Sync:TagPrefix +
/// version (built by .github/workflows/notifier.yml) is downloaded, checked against its feed's hashes and handed to
/// <see cref="NotifierPublisher.IngestAsync"/> as the new PILOT release. "Release to all" stays a person's decision
/// (Admin › Notifier). "[mandatory]" anywhere in the release notes marks it mandatory.
/// A status file next to the feed (not downloadable) records the last run; the third failure in a row notifies Admin.
public sealed class NotifierGithubSync(NotifierPublisher publisher, NotifierReleases releases, IConfiguration cfg,
    IHttpClientFactory http, IServiceScopeFactory scopes, ILogger<NotifierGithubSync> log)
{
    public const string StatusFile = "sync-status.json";
    public const string NotConfigured = "GitHub token not set (secret ipop-notifier-github).";
    public const string DefaultRepo = "HitterD/iPop-Integrated-Portal-for-Opertational-Performance-";

    public sealed record Status(DateTime? LastRunAt, DateTime? LastSuccessAt, string? LastError, int Failures,
        string? Remote, DateTime? RemotePublishedAt, string? Ingested);

    public sealed record RemoteRelease(string Tag, string Version, bool Mandatory, DateTime? PublishedAt, IReadOnlyDictionary<string, long> Assets);

    private string Repo => cfg["Notifier:Sync:Repo"] is { Length: > 0 } r ? r : DefaultRepo;
    private string Prefix => cfg["Notifier:Sync:TagPrefix"] is { Length: > 0 } p ? p : "notifier-v";

    public Status? ReadStatus()
    {
        try
        {
            var path = Path.Combine(releases.Folder, StatusFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<Status>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// One run. Returns 0 when the folder is up to date with GitHub (or nothing is published), 1 on failure.
    public async Task<int> RunAsync(DateTime nowUtc, CancellationToken ct)
    {
        var previous = ReadStatus();
        RemoteRelease? remote = null;
        try
        {
            var token = cfg["Notifier:Sync:Token"];
            if (string.IsNullOrWhiteSpace(token))
            {
                // Not set up yet (secret ipop-notifier-github): say so in Admin › Notifier, but don't page anyone.
                Write(new Status(nowUtc, previous?.LastSuccessAt, NotConfigured, 0, previous?.Remote, previous?.RemotePublishedAt, previous?.Ingested));
                return 0;
            }
            using var client = Client(token);
            remote = Pick(await GetJsonAsync(client, $"repos/{Repo}/releases?per_page=30", ct), Prefix);
            string? ingested = null;
            if (remote is not null && (publisher.Newest() is not { } newest || System.Version.Parse(remote.Version) > newest))
            {
                ingested = await DownloadAndIngestAsync(client, remote, nowUtc, ct);
                log.LogInformation("Notifier {Version} from GitHub {Tag} is now the pilot release", remote.Version, remote.Tag);
            }
            Write(new Status(nowUtc, nowUtc, null, 0, remote?.Version, remote?.PublishedAt, ingested ?? previous?.Ingested));
            return 0;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failures = (previous?.Failures ?? 0) + 1;
            log.LogError(e, "Notifier sync failed ({Failures} in a row)", failures);
            Write(new Status(nowUtc, previous?.LastSuccessAt, Short(e), failures, remote?.Version ?? previous?.Remote,
                remote?.PublishedAt ?? previous?.RemotePublishedAt, previous?.Ingested));
            if (failures == 3) await NotifyAsync(remote?.Version, Short(e), ct);
            return 1;
        }
    }

    /// Newest non-draft release with the prefix and a valid three-part version after it.
    public static RemoteRelease? Pick(JsonNode? list, string prefix)
    {
        RemoteRelease? best = null;
        foreach (var r in list?.AsArray() ?? [])
        {
            if (r?["draft"]?.GetValue<bool>() == true) continue;
            var tag = r?["tag_name"]?.GetValue<string>() ?? "";
            if (!tag.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var version = tag[prefix.Length..];
            if (!NotifierReleases.IsVersion(version)) continue;
            if (best is not null && System.Version.Parse(version) <= System.Version.Parse(best.Version)) continue;
            var assets = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var a in r?["assets"]?.AsArray() ?? [])
                if (a?["name"]?.GetValue<string>() is { } name && a["id"]?.GetValue<long>() is { } id) assets[name] = id;
            DateTime? at = r?["published_at"]?.GetValue<string>() is { } s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;
            var body = r?["body"]?.GetValue<string>() ?? "";
            best = new RemoteRelease(tag, version, body.Contains("[mandatory]", StringComparison.OrdinalIgnoreCase), at, assets);
        }
        return best;
    }

    private async Task<string> DownloadAndIngestAsync(HttpClient client, RemoteRelease remote, DateTime nowUtc, CancellationToken ct)
    {
        var incoming = Path.Combine(releases.Folder, "incoming", remote.Version);
        if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
        Directory.CreateDirectory(incoming);
        try
        {
            async Task Get(string name, bool required)
            {
                if (!remote.Assets.TryGetValue(name, out var id))
                {
                    if (required) throw new InvalidOperationException($"GitHub release {remote.Tag} has no {name}.");
                    return;
                }
                using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{Repo}/releases/assets/{id}");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                await using var file = File.Create(Path.Combine(incoming, name));
                await res.Content.CopyToAsync(file, ct);
            }

            await Get("releases.win.json", true);
            // Only packages the folder doesn't have yet (the previous full for deltas is usually already there).
            // A listed older package that is neither here nor in this release is dropped from the feed: clients
            // build deltas from their own installed package, so they only lose that one download option.
            var feedPath = Path.Combine(incoming, "releases.win.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(feedPath, ct));
            var feed = root?["Assets"]?.AsArray() ?? [];
            for (var i = feed.Count - 1; i >= 0; i--)
            {
                var name = feed[i]?["FileName"]?.GetValue<string>() ?? "";
                if (name.Length == 0 || Path.GetFileName(name) != name) throw new InvalidOperationException("Unsafe file name in the release feed.");
                if (File.Exists(Path.Combine(releases.Folder, name))) continue;
                if (remote.Assets.ContainsKey(name)) await Get(name, true);
                else if (feed[i]?["Version"]?.GetValue<string>() == remote.Version) throw new InvalidOperationException($"GitHub release {remote.Tag} has no {name}.");
                else feed.RemoveAt(i);
            }
            await File.WriteAllTextAsync(feedPath, root!.ToJsonString(), ct);
            foreach (var name in new[] { "RELEASES", "assets.win.json", publisher.Installer }) await Get(name, true);
            await Get(publisher.GpoMsi, false);
            await publisher.IngestAsync(incoming, remote.Version, remote.Mandatory, nowUtc, ct);
            return remote.Version;
        }
        finally { try { Directory.Delete(incoming, recursive: true); } catch (IOException) { } }
    }

    private HttpClient Client(string token)
    {
        var c = http.CreateClient(nameof(NotifierGithubSync));
        c.BaseAddress = new Uri("https://api.github.com/");
        c.Timeout = TimeSpan.FromMinutes(10);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ipop-notifier-sync");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    private static async Task<JsonNode?> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var res = await client.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)res.StatusCode} for the release list.");
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private async Task NotifyAsync(string? version, string error, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var notify = scope.ServiceProvider.GetRequiredService<INotificationGatewayService>();
            await notify.PublishInternalAsync("system", "system.ops", "Notifier update is stuck",
                $"The cluster could not fetch iPop Notifier {version ?? "releases"} from GitHub 3 times in a row: {error}",
                new NotificationTarget(Role: "Admin"), "high", "/admin", ct);
        }
        catch (Exception e) { log.LogWarning(e, "Could not notify Admin about the stuck Notifier sync"); }
    }

    private void Write(Status s)
    {
        try
        {
            Directory.CreateDirectory(releases.Folder);
            var path = Path.Combine(releases.Folder, StatusFile);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(s, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning(e, "Could not write the Notifier sync status"); }
    }

    private static string Short(Exception e) => e.Message.Length > 300 ? e.Message[..300] : e.Message;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
