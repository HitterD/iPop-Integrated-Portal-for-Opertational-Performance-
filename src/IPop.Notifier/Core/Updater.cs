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

    private VelopackAsset? pending;
    private UpdateManager? manager;
    private string? managerFeed;
    private string? checkedContext;
    private string Context => api.Server + "|" + (api.Session?.Nik ?? "signed-out");
    public bool ContextChanged => checkedContext != Context;
    private readonly NotifierUpdateState timing = new(Path.Combine(SessionStore.Folder, "update.json"));
    private bool mandatory;
    private readonly SemaphoreSlim gate = new(1, 1);

    public Ready? Pending => pending is null || ContextChanged ? null : new(pending.Version.ToString(), pending.NotesMarkdown, mandatory);
    public DateTime? AskedAt => timing.AskedAt;
    public void MarkAsked() => timing.MarkAsked(DateTime.UtcNow);
    public bool Asking { get; set; }

    public enum Result { NotInstalled, UpToDate, Ready, Failed }

    /// Looks for a newer version and downloads it. Nothing is installed here.
    public async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var context = Context;
            if (checkedContext != context)
            {
                pending = null;
                mandatory = false;
                checkedContext = null;
            }
            var mgr = Manager();
            if (!mgr.IsInstalled) { checkedContext = context; return Result.NotInstalled; }
            // Recover a prepared download before touching the network. Velopack startup auto-apply is off.
            if (pending is null && mgr.UpdatePendingRestart is { } local)
            {
                pending = local;
            }
            var (flag, channelVersion) = await ChannelAsync(ct);
            var next = await mgr.CheckForUpdatesAsync();
            if (Context != context) { pending = null; return Result.Failed; }
            if (next is null) { pending = null; checkedContext = context; return Result.UpToDate; }
            var version = next.TargetFullRelease.Version.ToString();
            if (pending?.Version.ToString() != version)
            {
                Log.Info("update", $"found {version}, downloading");
                await mgr.DownloadUpdatesAsync(next, cancelToken: ct);
                pending = next.TargetFullRelease;
                Log.Info("update", $"{version} ready" + (flag ? " (mandatory)" : ""));
            }
            if (Context != context) { pending = null; return Result.Failed; }
            timing.MarkReady(version, DateTime.UtcNow);
            mandatory = flag && channelVersion == version;
            checkedContext = context;
            return Result.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("update", "check failed: " + ex.GetType().Name + " " + ex.Message);
            // Do not expose an unvalidated/recovered package or failed timing write as installable.
            checkedContext = null;
            return Result.Failed;
        }
        finally { gate.Release(); }
    }

    public NotifierUpdatePolicy.Step NextStep(bool busy) => pending is null
        ? NotifierUpdatePolicy.Step.Wait
        : NotifierUpdatePolicy.Next(DateTime.UtcNow, timing.ReadyAt, AskedAt, mandatory, Asking, busy, Native.IdleTime());

    /// No install path bypasses the fresh call probe or the final local remote check.
    public async Task<NotifierUpdatePolicy.InstallResult> InstallAndRestartAsync(Func<Task<bool?>> inCall, Func<bool> remoteBusy)
    {
        await gate.WaitAsync();
        try
        {
            if (Pending is null || manager is null) throw new InvalidOperationException("No eligible downloaded update is ready. Check for updates again.");
            var context = Context;
            // A pilot download must not install for a different/signed-out account, or after a
            // release was withdrawn. Eligibility is checked before the final call/remote gate.
            var eligible = await manager.CheckForUpdatesAsync();
            if (Context != context || eligible?.TargetFullRelease.Version != pending!.Version)
            {
                pending = null;
                checkedContext = null;
                throw new InvalidOperationException("The available update changed. Check for updates again.");
            }
            return await NotifierUpdatePolicy.TryInstallAsync(inCall, remoteBusy, () =>
            {
                if (Context != context) throw new InvalidOperationException("Account changed while checking the update.");
                Log.Info("update", "installing " + pending.Version + " and restarting");
                manager.ApplyUpdatesAndRestart(pending, ["--updated"]);
            });
        }
        finally { gate.Release(); }
    }

    private UpdateManager Manager()
    {
        var fallback = AppInfo.UpdateFeed ?? new Uri(api.Server, "download/notifier/").ToString();
        // The channel source reads the current session for every check, including after sign-in/out.
        // Retain the manager used for downloading/applying; only a different server's feed replaces it.
        if (manager is null || managerFeed != fallback)
        {
            manager = new UpdateManager(new ChannelSource(api, fallback));
            managerFeed = fallback;
            pending = null;
            mandatory = false;
        }
        return manager;
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
            if (!api.IsSignedIn) return await web.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
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
