using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IPop.Api.Notifier;

/// Writes the Notifier release folder (the one <see cref="NotifierReleases"/> reads) exactly like
/// publish-notifier.ps1 does, so either can be used on the same folder:
///   Ingest(staged build)  = "publish to the pilot": packages into the folder, the build held in pilot/&lt;version&gt;
///                           with a receipt of hashes, releases.win.pilot.json, release.json.pilot last.
///   Promote()             = "-Release": validates the held build against its receipt, then the installer,
///                           the GPO .msi and releases.win.json for everyone, release.json.all last.
/// Every file is replaced through a temp file + rename in the same folder, so readers see old or new, never half.
public sealed class NotifierPublisher(NotifierReleases releases, IConfiguration cfg)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string PackId => cfg["Notifier:PackId"] is { Length: > 0 } id ? id : "iPopNotifier";
    private string Folder => releases.Folder;
    public string Installer => PackId + "-win-Setup.exe";
    /// Machine-wide bootstrapper for Group Policy (deploy/notifier/gpo). Optional: older builds have none.
    public string GpoMsi => PackId + "-win-GPO.msi";
    private string[] SideFiles => [Installer, "RELEASES", "assets.win.json"];

    public sealed class PublishException(string message) : Exception(message);

    public sealed record Outcome(string Version, string Channel);

    /// Every version this folder has ever seen (manifest, both feeds, packages, held pilots): never reuse one.
    public System.Version? Newest(bool includePackages = true)
    {
        var versions = new List<string?>();
        var state = releases.Read();
        versions.Add(state.All?.Version);
        versions.Add(state.Pilot?.Version);
        foreach (var feed in new[] { NotifierReleases.AllFeed, NotifierReleases.PilotFeed })
        {
            var path = Path.Combine(Folder, feed);
            if (!File.Exists(path)) continue;
            try { versions.AddRange(ReadFeed(path, checkPackId: false).Select(a => a.Version)); } catch (PublishException) { }
        }
        if (includePackages && Directory.Exists(Folder))
            foreach (var file in Directory.EnumerateFiles(Folder, "*.nupkg"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var dash = name.LastIndexOf('-');
                var prefix = PackId + "-";
                if (dash > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal)) versions.Add(name[prefix.Length..dash]);
            }
        var pilot = Path.Combine(Folder, "pilot");
        if (Directory.Exists(pilot)) versions.AddRange(Directory.EnumerateDirectories(pilot).Select(Path.GetFileName));
        return versions.Where(NotifierReleases.IsVersion).Select(v => System.Version.Parse(v!)).DefaultIfEmpty().Max();
    }

    /// A finished build (vpk pack output + optional GPO msi) in <paramref name="staged"/> becomes the pilot release.
    public async Task<Outcome> IngestAsync(string staged, string version, bool mandatory, DateTime nowUtc, CancellationToken ct = default)
    {
        if (!NotifierReleases.IsVersion(version)) throw new PublishException($"Invalid version '{version}'.");
        var target = System.Version.Parse(version);
        await Gate.WaitAsync(ct);
        try
        {
            using var fileLock = Lock();
            // A build of this version that was held but never committed (crash/restart mid-ingest) is retried:
            // its folder goes; packages already copied must still match byte for byte (checked below).
            var state = releases.Read();
            var partial = Path.Combine(Folder, "pilot", version);
            if (Directory.Exists(partial) && state.Pilot?.Version != version && state.All?.Version != version)
                Directory.Delete(partial, recursive: true);
            if (Newest(includePackages: false) is { } newest && target <= newest)
                throw new PublishException($"Version {version} is not newer than already published/held {newest}.");

            var feedPath = Path.Combine(staged, "releases.win.json");
            var assets = ReadFeed(feedPath, checkPackId: true);
            if (!assets.Any(a => a.Version == version && a.Type == "Full")) throw new PublishException($"No full package for {version}.");
            if (assets.Any(a => System.Version.Parse(a.Version) > target)) throw new PublishException("Packaged feed is newer than the release version.");
            // Older packages the feed still lists (the previous full, for deltas) may already be in the folder
            // instead of the build: CI uploads only what is new.
            string Source(string name) => File.Exists(Path.Combine(staged, name)) ? Path.Combine(staged, name) : Path.Combine(Folder, name);
            foreach (var a in assets)
            {
                var file = Source(a.FileName);
                RequireFile(file);
                if (a.Sha256 is { Length: > 0 } sha && !string.Equals(Hash(file), sha, StringComparison.OrdinalIgnoreCase))
                    throw new PublishException($"Package hash does not match the feed: {a.FileName}");
            }
            foreach (var name in SideFiles) RequireFile(Path.Combine(staged, name));
            var withMsi = File.Exists(Path.Combine(staged, GpoMsi));

            // Held build + receipt first, then packages, then the pilot feed, then release.json (the commit).
            var held = Path.Combine(Folder, "pilot", version);
            Directory.CreateDirectory(held);
            var heldFiles = SideFiles.Concat(withMsi ? [GpoMsi] : []).Append("releases.win.json").ToList();
            var hashes = new JsonObject();
            foreach (var name in heldFiles) hashes[name] = Hash(Path.Combine(staged, name));
            foreach (var a in assets) hashes[a.FileName] = Hash(Source(a.FileName));
            foreach (var name in heldFiles) Install(Path.Combine(staged, name), Path.Combine(held, name));
            WriteText(Path.Combine(held, "receipt.json"), new JsonObject { ["version"] = version, ["packageId"] = PackId, ["files"] = hashes }.ToJsonString(Indented));

            foreach (var a in assets)
            {
                var source = Source(a.FileName);
                var destination = Path.Combine(Folder, a.FileName);
                if (source == destination) continue;
                if (File.Exists(destination))
                {
                    if (Hash(destination) != Hash(source)) throw new PublishException($"Refusing to overwrite published package: {a.FileName}");
                }
                else Install(source, destination);
            }
            Install(feedPath, Path.Combine(Folder, NotifierReleases.PilotFeed));

            var manifest = ReadManifest();
            manifest["pilot"] = Entry(version, mandatory, nowUtc);
            WriteText(Path.Combine(Folder, NotifierReleases.Manifest), manifest.ToJsonString(Indented));
            return new Outcome(version, "pilot");
        }
        finally { Gate.Release(); }
    }

    /// The pilot becomes everyone's release (installer, msi, feed), after checking nothing changed since it was held.
    public async Task<Outcome> PromoteAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var fileLock = Lock();
            var manifest = ReadManifest();
            var pilot = manifest["pilot"];
            var version = pilot?["version"]?.GetValue<string>();
            if (!NotifierReleases.IsVersion(version)) throw new PublishException("No pilot release to promote.");
            var all = manifest["all"]?["version"]?.GetValue<string>();
            var publicNewest = new[] { all }.Concat(File.Exists(Path.Combine(Folder, NotifierReleases.AllFeed))
                    ? ReadFeed(Path.Combine(Folder, NotifierReleases.AllFeed), checkPackId: false).Select(a => a.Version) : [])
                .Where(NotifierReleases.IsVersion).Select(v => System.Version.Parse(v!)).DefaultIfEmpty(new System.Version(0, 0, 0)).Max()!;
            if (System.Version.Parse(version!) <= publicNewest) throw new PublishException("Pilot is not newer than the public release.");

            var held = Path.Combine(Folder, "pilot", version!);
            var receiptPath = Path.Combine(held, "receipt.json");
            RequireFile(receiptPath);
            var receipt = JsonNode.Parse(File.ReadAllText(receiptPath));
            if (receipt?["version"]?.GetValue<string>() != version || receipt?["packageId"]?.GetValue<string>() != PackId)
                throw new PublishException("Pilot receipt version/package identity mismatch.");
            var files = receipt?["files"]?.AsObject() ?? throw new PublishException("Pilot receipt has no files.");
            var withMsi = files.ContainsKey(GpoMsi);
            var heldFiles = SideFiles.Concat(withMsi ? [GpoMsi] : []).Append("releases.win.json").ToList();
            foreach (var name in heldFiles) CheckHash(Path.Combine(held, name), files, name);
            if (Hash(Path.Combine(Folder, NotifierReleases.PilotFeed)) != Hash(Path.Combine(held, "releases.win.json")))
                throw new PublishException("Pilot feed changed since it was held.");
            var assets = ReadFeed(Path.Combine(held, "releases.win.json"), checkPackId: true);
            if (!assets.Any(a => a.Version == version && a.Type == "Full")) throw new PublishException($"No full package for {version}.");
            foreach (var a in assets) CheckHash(Path.Combine(Folder, a.FileName), files, a.FileName);

            foreach (var name in SideFiles.Concat(withMsi ? [GpoMsi] : [])) Install(Path.Combine(held, name), Path.Combine(Folder, name));
            var feedAll = Path.Combine(Folder, NotifierReleases.AllFeed);
            if (File.Exists(feedAll)) Install(feedAll, feedAll + ".bak");
            Install(Path.Combine(held, "releases.win.json"), feedAll);

            manifest["all"] = Entry(version!, pilot?["mandatory"]?.GetValue<bool>() == true, nowUtc);
            manifest["pilot"] = null;
            WriteText(Path.Combine(Folder, NotifierReleases.Manifest), manifest.ToJsonString(Indented));
            return new Outcome(version!, "all");
        }
        finally { Gate.Release(); }
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public sealed record Asset(string Version, string Type, string FileName, string? Sha256);

    private List<Asset> ReadFeed(string path, bool checkPackId)
    {
        RequireFile(path);
        JsonArray? list;
        try { list = JsonNode.Parse(File.ReadAllText(path))?["Assets"]?.AsArray(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new PublishException($"Unreadable feed {Path.GetFileName(path)}."); }
        if (list is null || list.Count == 0) throw new PublishException($"No assets in feed {Path.GetFileName(path)}.");
        var result = new List<Asset>();
        foreach (var n in list)
        {
            var version = n?["Version"]?.GetValue<string>();
            var file = n?["FileName"]?.GetValue<string>() ?? "";
            if (!NotifierReleases.IsVersion(version)) throw new PublishException($"Invalid version in feed {Path.GetFileName(path)}.");
            if (checkPackId && n?["PackageId"]?.GetValue<string>() != PackId) throw new PublishException($"Feed package identity must be {PackId}.");
            if (file.Length == 0 || Path.GetFileName(file) != file || file.IndexOfAny(['/', '\\', ':']) >= 0 || !file.EndsWith(".nupkg", StringComparison.Ordinal))
                throw new PublishException($"Unsafe feed filename in {Path.GetFileName(path)}.");
            result.Add(new Asset(version!, n?["Type"]?.GetValue<string>() ?? "", file, n?["SHA256"]?.GetValue<string>()));
        }
        return result;
    }

    private JsonObject ReadManifest()
    {
        var path = Path.Combine(Folder, NotifierReleases.Manifest);
        if (File.Exists(path))
        {
            var m = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new PublishException("Unreadable release.json.");
            if (m["all"] is null) throw new PublishException("Release manifest must have an all entry.");
            return m;
        }
        var all = releases.Read().All;
        return new JsonObject { ["all"] = Entry(all?.Version ?? "0.0.0", false, null), ["pilot"] = null };
    }

    private static JsonObject Entry(string version, bool mandatory, DateTime? at) => new()
    {
        ["version"] = version,
        ["mandatory"] = mandatory,
        ["publishedAt"] = at?.ToUniversalTime().ToString("o"),
    };

    private void CheckHash(string path, JsonObject files, string name)
    {
        RequireFile(path);
        if (files[name]?.GetValue<string>() is not { } recorded || !string.Equals(Hash(path), recorded, StringComparison.OrdinalIgnoreCase))
            throw new PublishException($"Pilot artifact hash changed: {name}");
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new PublishException($"Missing or empty artifact: {Path.GetFileName(path)}");
    }

    public static string Hash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private static void Install(string source, string destination)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp);
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WriteText(string destination, string text)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// Same lock file as publish-notifier.ps1, so the script and the server never write at the same time.
    private FileStream Lock()
    {
        Directory.CreateDirectory(Folder);
        try { return new FileStream(Path.Combine(Folder, ".publish.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new PublishException("Another publisher holds the release lock."); }
    }
}
