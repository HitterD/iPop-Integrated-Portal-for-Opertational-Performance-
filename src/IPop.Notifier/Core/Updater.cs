using System.Text.Json;
using IPop.Application.Notifier;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace IPop.Notifier.Core;

/// Updates with a heads-up (1.3.0): the new version downloads quietly, then the person is asked
/// ("Update now / Later"). The rules for when to ask, ask again and install anyway are <see cref="NotifierUpdatePolicy"/>.
/// Signed in, the feed comes from the API (pilot channel for ICT/Admin, everyone else the released version);
/// signed out or on an older server, the public /download/notifier/ feed.
public sealed class Updater(IPopClient api)
{
    public sealed record Ready(string Version, string? Notes, bool Mandatory);

    private UpdateInfo? pending;
    private DateTime readyAt;
    private bool mandatory;
    private readonly SemaphoreSlim gate = new(1, 1);

    public Ready? Pending => pending is null ? null : new(pending.TargetFullRelease.Version.ToString(), pending.TargetFullRelease.NotesMarkdown, mandatory);
    public DateTime? AskedAt { get; set; }
    public bool Asking { get; set; }

    public enum Result { NotInstalled, UpToDate, Ready, Failed }

    /// Looks for a newer version and downloads it. Nothing is installed here.
    public async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var mgr = Manager();
            if (!mgr.IsInstalled) return Result.NotInstalled;
            var (flag, _) = await ChannelAsync(ct);
            var next = await mgr.CheckForUpdatesAsync();
            if (next is null) { pending = null; return Result.UpToDate; }
            var version = next.TargetFullRelease.Version.ToString();
            if (pending?.TargetFullRelease.Version.ToString() != version)
            {
                Log.Info("update", $"found {version}, downloading");
                await mgr.DownloadUpdatesAsync(next, cancelToken: ct);
                pending = next;
                readyAt = DateTime.UtcNow;
                AskedAt = null;
                Log.Info("update", $"{version} ready" + (flag ? " (mandatory)" : ""));
            }
            mandatory = flag;
            return Result.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("update", "check failed: " + ex.GetType().Name + " " + ex.Message);
            return Result.Failed;
        }
        finally { gate.Release(); }
    }

    public NotifierUpdatePolicy.Step NextStep(bool busy) => pending is null
        ? NotifierUpdatePolicy.Step.Wait
        : NotifierUpdatePolicy.Next(DateTime.UtcNow, readyAt, AskedAt, mandatory, Asking, busy, Native.IdleTime());

    /// Restarts into the new version (signed in: the session is on disk).
    public void InstallAndRestart()
    {
        if (pending is null) return;
        Log.Info("update", "installing " + pending.TargetFullRelease.Version + " and restarting");
        Manager().ApplyUpdatesAndRestart(pending.TargetFullRelease, ["--updated"]);
    }

    private UpdateManager Manager()
    {
        var fallback = AppInfo.UpdateFeed ?? new Uri(api.Server, "download/notifier/").ToString();
        IUpdateSource source = api.IsSignedIn && AppInfo.UpdateFeed is null ? new ChannelSource(api, fallback) : new SimpleWebSource(fallback);
        return new UpdateManager(source);
    }

    /// "mandatory" for the version this person should run (false on an older server).
    private async Task<(bool Mandatory, string? Version)> ChannelAsync(CancellationToken ct)
    {
        if (!api.IsSignedIn) return (false, null);
        var n = await api.GetJsonAsync("api/notifier/update", ct);
        return (n?["mandatory"]?.GetValue<bool>() == true, n?["version"]?.GetValue<string>());
    }

    /// Feed from the API for the signed-in person (pilot or all); files still come from /download/notifier/.
    private sealed class ChannelSource(IPopClient api, string files) : IUpdateSource
    {
        private readonly SimpleWebSource web = new(files);

        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            try
            {
                using var res = await api.SendAsync(HttpMethod.Get, "api/notifier/update/feed");
                if (res.IsSuccessStatusCode) return VelopackAssetFeed.FromJson(await res.Content.ReadAsStringAsync());
                Log.Warn("update", $"channel feed answered {(int)res.StatusCode}; using the public feed");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            {
                Log.Warn("update", "channel feed failed: " + ex.Message);
            }
            return await web.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
            => web.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
    }
}
