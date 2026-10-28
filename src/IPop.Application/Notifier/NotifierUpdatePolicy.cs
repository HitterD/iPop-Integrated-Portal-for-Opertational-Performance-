namespace IPop.Application.Notifier;

/// When the iPop Notifier installs an update it has already downloaded (grilled 2026-10-26): ask first
/// ("Update now / Later"), ask again 4 hours after Later, install by itself after 24 hours once the PC has been idle
/// a few minutes, and a mandatory release installs straight away. Never during remote help or a call.
/// Shared by the app (linked into IPop.Notifier) and the tests.
public static class NotifierUpdatePolicy
{
    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromHours(4);
    public static readonly TimeSpan InstallAnywayAfter = TimeSpan.FromHours(24);
    public static readonly TimeSpan IdleBeforeQuietInstall = TimeSpan.FromMinutes(5);

    public enum Step { Wait, Ask, InstallNow, InstallQuietly }
    public enum InstallResult { Installed, Busy, UnknownCallStatus }

    /// The active-call endpoint returns an empty 200 or JSON null when idle. Errors, malformed JSON,
    /// and unexpected shapes are unknown, not idle.
    public static bool? ReadCallStatus(int statusCode, string body)
    {
        if (statusCode != 200) return null;
        if (body.Length == 0) return false;
        try
        {
            var value = System.Text.Json.Nodes.JsonNode.Parse(body);
            return value is null ? false : value is System.Text.Json.Nodes.JsonObject { Count: > 0 } ? true : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// Final safety gate for ALL install paths, including Update now and mandatory updates.
    /// Probe calls afresh, then check local remote activity after the awaited probe, immediately before apply.
    public static async Task<InstallResult> TryInstallAsync(Func<Task<bool?>> inCall, Func<bool> remoteBusy, Action apply)
    {
        bool? call;
        try { call = await inCall(); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return InstallResult.UnknownCallStatus;
        }
        if (call is null) return InstallResult.UnknownCallStatus;
        if (call.Value || remoteBusy()) return InstallResult.Busy;
        apply();
        return InstallResult.Installed;
    }

    /// <param name="readyAt">When this version finished downloading on this PC.</param>
    /// <param name="askedAt">When the person last saw the update prompt (null = never).</param>
    /// <param name="asking">The prompt is open right now.</param>
    /// <param name="busy">Remote help or a call is running.</param>
    /// <param name="idle">Time since the last keyboard/mouse input.</param>
    public static Step Next(DateTime now, DateTime readyAt, DateTime? askedAt, bool mandatory, bool asking, bool busy, TimeSpan idle)
    {
        if (busy) return Step.Wait;
        if (mandatory) return Step.InstallNow;
        if (now - readyAt >= InstallAnywayAfter && idle >= IdleBeforeQuietInstall) return Step.InstallQuietly;
        if (asking) return Step.Wait;
        if (askedAt is null || now - askedAt.Value >= AskAgainAfter) return Step.Ask;
        return Step.Wait;
    }
}
