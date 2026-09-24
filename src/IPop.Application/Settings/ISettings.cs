namespace IPop.Application.Settings;

/// Admin-tunable settings persisted in AppSettings (v6 foundation spec §7).
public interface ISettings
{
    Task<T> GetAsync<T>(string key, T fallback, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, string? actorNik, CancellationToken ct = default);
}

/// Known keys and their defaults. Modules add theirs here so defaults live in one place.
public static class SettingKeys
{
    public const string HrisAutoSyncCron = "hris.autoSync.cron";
    public const string HrisProtectManualEdits = "hris.protectManualEdits";
    public const string HrisNotifyOnFailure = "hris.notifyOnFailure";
    public const string NotifDndStart = "notif.dnd.start";
    public const string NotifDndEnd = "notif.dnd.end";
    public const string NotifDigestTime = "notif.digest.time";

    public static readonly IReadOnlyDictionary<string, object> Defaults = new Dictionary<string, object>
    {
        [HrisAutoSyncCron] = "0 2 * * *",
        [HrisProtectManualEdits] = true,
        [HrisNotifyOnFailure] = true,
        [NotifDndStart] = "22:00",
        [NotifDndEnd] = "07:00",
        [NotifDigestTime] = "07:30",
    };
}
