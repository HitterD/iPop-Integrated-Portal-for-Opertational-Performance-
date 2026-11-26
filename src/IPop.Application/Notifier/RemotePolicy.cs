using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPop.Application.Notifier;

/// Remote help company policy (Settings › Remote, Administrators only; BIGIS #8 v3). Stored server-side in
/// AppSettings ("remote.policy") and enforced by the server: connect, prompt, unattended, lockout, files, history.
/// Defaults are the owner-approved ones from the v3 mockup README. Out-of-range values snap to the nearest option.
public sealed record RemotePolicy
{
    public const string Key = "remote.policy";

    public bool RemoteOn { get; init; } = true;
    /// "role" = every ICT/Admin person (minus the blocked ones), "list" = only the people ticked in <see cref="People"/>.
    public string Mode { get; init; } = "role";
    /// Per-person switch (user id → allowed). In "role" mode false = blocked; in "list" mode true = allowed.
    public Dictionary<Guid, bool> People { get; init; } = [];
    public bool RequirePermission { get; init; } = true;
    public int AskSeconds { get; init; } = 30;
    public bool AllowUnattended { get; init; } = true;
    public int MinPassword { get; init; } = 10;
    public RemotePerms DefaultPerms { get; init; } = new(true, true, false);
    public string Quality { get; init; } = "auto";
    public int MaxFileMB { get; init; } = 2048;
    public int ReconnectAttempts { get; init; } = 6;
    public int ReconnectSeconds { get; init; } = 60;
    public int LockAttempts { get; init; } = 5;
    public int LockMinutes { get; init; } = 15;
    public int RatePerHour { get; init; } = 10;
    public bool NotifyAdminOnLock { get; init; } = true;
    public int RetentionDays { get; init; } = 365;

    public static readonly int[] AskOptions = [15, 30, 60, 120];
    public static readonly int[] MinPasswordOptions = [8, 10, 12, 16];
    public static readonly int[] MaxFileOptions = [200, 1024, 2048, 5120];
    public static readonly int[] ReconnectAttemptOptions = [3, 5, 6, 8];
    public static readonly int[] ReconnectSecondOptions = [15, 30, 60];
    public static readonly int[] LockAttemptOptions = [3, 5, 10];
    public static readonly int[] LockMinuteOptions = [5, 15, 60];
    public static readonly int[] RateOptions = [5, 10, 20, 50];
    public static readonly int[] RetentionOptions = [90, 180, 365, 730];

    public long MaxFileBytes => MaxFileMB * 1024L * 1024;

    /// Only known values survive; anything else becomes the nearest allowed option (or the default).
    public RemotePolicy Normalize() => this with
    {
        Mode = Mode is "list" ? "list" : "role",
        People = People ?? [],
        AskSeconds = Snap(AskSeconds, AskOptions),
        MinPassword = Snap(MinPassword, MinPasswordOptions),
        DefaultPerms = DefaultPerms ?? new(true, true, false),
        Quality = Quality is "smooth" or "quality" ? Quality : "auto",
        MaxFileMB = Snap(MaxFileMB, MaxFileOptions),
        ReconnectAttempts = Snap(ReconnectAttempts, ReconnectAttemptOptions),
        ReconnectSeconds = Snap(ReconnectSeconds, ReconnectSecondOptions),
        LockAttempts = Snap(LockAttempts, LockAttemptOptions),
        LockMinutes = Snap(LockMinutes, LockMinuteOptions),
        RatePerHour = Snap(RatePerHour, RateOptions),
        RetentionDays = Snap(RetentionDays, RetentionOptions),
    };

    /// May this ICT/Admin person use Remote under the policy (role check done by the caller)?
    public bool Allows(Guid userId) => Mode == "list" ? People.GetValueOrDefault(userId) : People.GetValueOrDefault(userId, true);

    private static int Snap(int v, int[] options) => options.OrderBy(o => Math.Abs(o - v)).First();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static RemotePolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return (JsonSerializer.Deserialize<RemotePolicy>(json, Json) ?? new()).Normalize(); }
        catch (JsonException) { return new(); }
    }
}

/// The three things a user can allow (prompt) or an Admin can allow on a PC.
public sealed record RemotePerms(bool Control, bool Clipboard, bool Files)
{
    public static RemotePerms All => new(true, true, true);
    public static RemotePerms Parse(string? list)
    {
        if (list is null) return All;
        var set = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()).ToHashSet();
        return new(set.Contains("control"), set.Contains("clipboard"), set.Contains("files"));
    }
    [JsonIgnore] public string List => string.Join(',', new[] { Control ? "control" : null, Clipboard ? "clipboard" : null, Files ? "files" : null }.OfType<string>());
    public RemotePerms And(RemotePerms o) => new(Control && o.Control, Clipboard && o.Clipboard, Files && o.Files);
}
