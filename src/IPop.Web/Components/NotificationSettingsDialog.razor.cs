using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Components;

public partial class NotificationSettingsDialog
{
    private string activeTab = "general";
    private bool saving;
    private IDisposable? escReg;
    internal NotificationPreferences settings = new();
    private List<string> extraMutedTypes = [];

    internal List<(string Value, string Label)> days =
    [
        ("mon", "Mon"),
        ("tue", "Tue"),
        ("wed", "Wed"),
        ("thu", "Thu"),
        ("fri", "Fri"),
        ("sat", "Sat"),
        ("sun", "Sun"),
    ];

    private static readonly IReadOnlyList<NotificationType> AllNotificationTypes = NotificationCategories.All
        .Select(c => new NotificationType(c.Id, c.Label, c.Description, c.Icon))
        .ToList();

    internal IReadOnlyList<NotificationType> notificationTypes => AllNotificationTypes;

    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
        Push.Changed += OnPushChanged;
        Phone.Changed += OnPhoneChanged;
    }

    private void OnPhoneChanged() => _ = InvokeAsync(StateHasChanged);

    private void OnStateChanged() => _ = InvokeAsync(async () =>
    {
        if (State.IsOpen) await OpenAsync();
        else { escReg?.Dispose(); escReg = null; }
        StateHasChanged();
    });

    /// Fresh values every time it opens, so Cancel really discards unsaved edits.
    private async Task OpenAsync()
    {
        // Phone: the BottomSheet registers its own Back entry; a second one would need two Back presses.
        if (!Phone.IsPhone) escReg ??= Overlays.Push(() => { State.Close(); return Task.CompletedTask; });
        activeTab = "general";
        settings = new NotificationPreferences();
        extraMutedTypes = [];
        historyItems = [];
        StateHasChanged();
        await Push.RefreshAsync();
        await LoadPush();
        await LoadPreferences();
        await LoadLocalPreferences();
        await LoadSoundPrefs();
        StateHasChanged();
        await SyncBrowserTimezone();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (State.IsOpen) await JS.InvokeVoidAsync("ipop.icons");
    }

    internal void Close() => State.Close();

    /// Deteksi zona waktu browser dan simpan ke server bila berbeda — jam senyap
    /// dievaluasi backend dalam zona waktu pengguna, bukan server.
    private async Task SyncBrowserTimezone()
    {
        try
        {
            var tz = await JS.InvokeAsync<string>("eval", "Intl.DateTimeFormat().resolvedOptions().timeZone");
            if (string.IsNullOrWhiteSpace(tz)) return;
            // .NET di Windows memakai ID Windows ("SE Asia Standard Time"), browser memakai IANA ("Asia/Jakarta").
            var windowsId = MapIanaToWindows(tz);
            if (windowsId is null) return;
            if (string.Equals(_serverTimeZone, windowsId, StringComparison.OrdinalIgnoreCase)) return;
            var res = await Auth.SendAsync(HttpMethod.Put, "/api/notifications/v1/preferences/timezone", new { TimeZoneId = windowsId });
            if (res.IsSuccessStatusCode) _serverTimeZone = windowsId;
        }
        catch { /* best-effort */ }
    }

    private string? _serverTimeZone;

    private static string? MapIanaToWindows(string iana) => iana switch
    {
        "Asia/Jakarta" or "Asia/Pontianak" => "SE Asia Standard Time",
        "Asia/Makassar" => "Singapore Standard Time", // WITA — closest Windows id at UTC+8
        "Asia/Jayapura" => "Tokyo Standard Time",     // WIT — closest Windows id at UTC+9
        _ => null // biarkan server default bila di luar Indonesia
    };

    // ── Windows toasts (Web Push) ──
    internal bool pushBusy;
    internal bool pushPreview = true;
    internal string? pushTestNote;
    private List<PushDeviceDto> pushDevices = [];

    private sealed record PushDeviceDto(Guid Id, string Label, string Endpoint, DateTime CreatedAt, DateTime LastSeenAt);
    private sealed record PushPreferencesDto(bool ShowPreview);
    private PushDiagnosticsDto? pushDiagnostics;
    private sealed record PushDiagnosticsDto(bool Enabled, List<PushAttemptDto> Recent);
    private sealed record PushAttemptDto(Guid DeliveryId, Guid JobId, string Kind, Guid DeviceId, string Status, int Attempts, DateTime CreatedAt, DateTime? AcceptedAt, string? Code,
        string? DeviceOutcome = null, DateTime? DeviceReportedAt = null, string? DeviceDetail = null);

    internal async Task CheckPushHealth()
    {
        pushBusy = true;
        try { await Push.RefreshAsync(); await LoadPush(); }
        finally { pushBusy = false; }
    }

    private async Task LoadPush()
    {
        pushDevices = await Auth.GetAsync<List<PushDeviceDto>>("/api/push/devices") ?? [];
        try { notifierPcs = (await Auth.GetAsync<NotifierMe>("/api/notifier/me"))?.OnlinePcs ?? []; } catch { notifierPcs = []; }
        pushPreview = (await Auth.GetAsync<PushPreferencesDto>("/api/push/preferences"))?.ShowPreview ?? true;
        var query = Push.CurrentDeviceId is { } id ? $"?current={id}" : "";
        pushDiagnostics = await Auth.GetAsync<PushDiagnosticsDto>("/api/push/diagnostics" + query);
    }

    private void OnPushChanged() => _ = InvokeAsync(async () => { StateHasChanged(); await JS.InvokeVoidAsync("ipop.icons"); });

    internal async Task TogglePush(ChangeEventArgs e)
    {
        pushBusy = true;
        if (e.Value is true) await Push.EnableAsync();
        else await Push.DisableAsync();
        await LoadPush();
        pushBusy = false;
        await JS.InvokeVoidAsync("ipop.icons");
    }

    internal async Task TogglePushPreview(ChangeEventArgs e)
    {
        pushPreview = e.Value is true;
        using var res = await Auth.SendAsync(HttpMethod.Put, "/api/push/preferences", new PushPreferencesDto(pushPreview));
        if (!res.IsSuccessStatusCode) pushPreview = !pushPreview;
    }

    internal async Task SendPushTest()
    {
        pushBusy = true;
        using var res = await Auth.SendAsync(HttpMethod.Post, "/api/push/test");
        pushTestNote = res.IsSuccessStatusCode
            ? "Sent — the toast should appear within a few seconds. Not showing? Check Focus Assist / Do not disturb in Windows."
            : "Couldn't send the test. Try again.";
        pushBusy = false;
    }

    private async Task RemovePushDevice(PushDeviceDto d)
    {
        if (d.Endpoint == Push.Endpoint) await Push.DisableAsync();
        else await Auth.SendAsync(HttpMethod.Delete, $"/api/push/devices/{d.Id}");
        await LoadPush();
        await JS.InvokeVoidAsync("ipop.icons");
    }

    public void Dispose()
    {
        State.Changed -= OnStateChanged;
        Push.Changed -= OnPushChanged;
        Phone.Changed -= OnPhoneChanged;
        escReg?.Dispose();
    }

    private async Task LoadPreferences()
    {
        try
        {
            var response = await Auth.GetAsync<NotificationPreferenceDto>("/api/notifications/v1/preferences");
            if (response is not null)
            {
                settings.QuietHoursEnabled = response.QuietHoursEnabled;
                if (TimeOnly.TryParse(response.QuietStart, out var start)) settings.QuietStart = start;
                if (TimeOnly.TryParse(response.QuietEnd, out var end)) settings.QuietEnd = end;

                // Load QuietDays dan AllowImportant dari backend
                if (!string.IsNullOrEmpty(response.QuietDays))
                {
                    settings.QuietDays = response.QuietDays.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                }
                settings.AllowImportant = response.AllowImportant;

                // MutedTypes = types that are DISABLED (inverse of EnabledTypes), aligned with NotificationCategories.All
                var muted = NotificationCategories.ParseMuted(response.MutedTypes);
                var canonical = NotificationCategories.All.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                extraMutedTypes = muted.Where(m => !canonical.Contains(m)).ToList();
                settings.EnabledTypes = NotificationCategories.All
                    .Where(t => !muted.Contains(t.Id))
                    .Select(t => t.Id)
                    .ToList();
            }
        }
        catch
        {
            // Use defaults on error
        }
    }

    private string BuildMutedTypesCsv() => string.Join(",",
        NotificationCategories.All
            .Where(t => !settings.EnabledTypes.Contains(t.Id, StringComparer.OrdinalIgnoreCase))
            .Select(t => t.Id)
            .Concat(extraMutedTypes));

    private async Task SetTab(string tab)
    {
        activeTab = tab;
        if (tab == "history" && historyItems.Count == 0)
        {
            await LoadHistory();
        }
    }

    internal void ToggleDay(string day)
    {
        if (settings.QuietDays.Contains(day))
            settings.QuietDays.Remove(day);
        else
            settings.QuietDays.Add(day);
    }

    // ── Phone (NF2): instant save, sound picker sheet ──
    internal string? soundSheet;
    private bool phoneSaving;

    internal string PhonePushBlocked => !Push.ServerEnabled ? "Not enabled on the iPop server yet."
        : !Push.Secure || Push.Permission == "unsupported" ? "Open iPop via https://ipop.santos.co.id or the installed app."
        : "Blocked in this browser. Allow notifications for iPop in the browser's site settings.";

    internal string QuietSummary => !settings.QuietHoursEnabled ? "Off"
        : $"{settings.QuietStart:HH:mm} – {settings.QuietEnd:HH:mm}" + (settings.QuietDays.Count == 7 ? " · every day" : $" · {settings.QuietDays.Count} days");

    internal static string SoundName(string file) => file == "" ? "None" : SoundChoices.FirstOrDefault(c => c.File == file).Name ?? file;

    /// Apply a change and save it at once (the phone list has no Save button).
    internal async Task PhoneToggle(Action change)
    {
        change();
        StateHasChanged();
        await PhoneSaveAll();
    }

    private async Task PhoneSaveAll()
    {
        if (phoneSaving) return;
        phoneSaving = true;
        try
        {
            var dto = new
            {
                QuietHoursEnabled = settings.QuietHoursEnabled,
                QuietStart = settings.QuietStart?.ToString("HH:mm"),
                QuietEnd = settings.QuietEnd?.ToString("HH:mm"),
                QuietDays = string.Join(",", settings.QuietDays),
                AllowImportant = settings.AllowImportant,
                MutedTypes = BuildMutedTypesCsv()
            };
            using var res = await Auth.SendAsync(HttpMethod.Put, "/api/notifications/v1/preferences", dto);
            if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't save. Check your connection and try again.", ToastKind.Error); return; }
            await SaveLocalPreferences();
            await SaveSoundPrefs();
            await Notifs.LoadPreferencesAsync();
        }
        catch { Toasts.Show("Couldn't save. Check your connection and try again.", ToastKind.Error); }
        finally { phoneSaving = false; }
    }

    internal Task PhoneSaveSounds() => SaveSoundPrefs();
    internal void SetVolume(ChangeEventArgs e) { if (int.TryParse(e.Value?.ToString(), out var v)) soundVolume = v; }
    internal PushState Push => PushSvc;

    internal async Task PhonePickSound(string module, string? file)
    {
        await PickSound(module, file);
        await SaveSoundPrefs();
        soundSheet = null;
    }

    internal async Task PhoneReset()
    {
        ResetToDefault();
        await PhoneSaveAll();
        Toasts.Show("Notification settings reset", ToastKind.Success);
    }

    internal void ToggleType(string typeId)
    {
        var existing = settings.EnabledTypes.FirstOrDefault(x => x.Equals(typeId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            settings.EnabledTypes.Remove(existing);
        else
            settings.EnabledTypes.Add(typeId);
    }

    private void ResetToDefault()
    {
        settings = new NotificationPreferences();
        extraMutedTypes = [];
        soundVolume = 80;
        soundMap = SoundModules.ToDictionary(m => m.Key, m => m.Key);
    }

    private async Task SaveSettings()
    {
        if (saving) return;
        saving = true;
        try
        {
            // Map to API DTO — semua field yang didukung backend
            var dto = new
            {
                QuietHoursEnabled = settings.QuietHoursEnabled,
                QuietStart = settings.QuietStart?.ToString("HH:mm"),
                QuietEnd = settings.QuietEnd?.ToString("HH:mm"),
                QuietDays = string.Join(",", settings.QuietDays),
                AllowImportant = settings.AllowImportant,
                MutedTypes = BuildMutedTypesCsv()
            };

            var response = await Auth.SendAsync(HttpMethod.Put, "/api/notifications/v1/preferences", dto);
            if (response.IsSuccessStatusCode)
            {
                // Simpan preferensi lokal yang tidak didukung backend ke localStorage,
                // lalu muat ulang preferensi di NotificationState agar banner/toast/badge
                // langsung mengikuti perubahan tanpa menunggu reload halaman.
                await SaveLocalPreferences();
                await SaveSoundPrefs();
                await Notifs.LoadPreferencesAsync();
                State.Close();
                Toasts.Show("Notification settings saved", ToastKind.Success);
            }
            else Toasts.Show("Couldn't save the settings. Please try again.", ToastKind.Error);
        }
        catch (Exception ex)
        {
            Toasts.Show($"Couldn't save the settings: {ex.Message}", ToastKind.Error);
        }
        finally { saving = false; }
    }

    private async Task SaveLocalPreferences()
    {
        try
        {
            // Hanya simpan preferensi yang TIDAK didukung backend (EnableNotifications, PlaySound, ShowBadge, ShowPreview)
            var local = new
            {
                settings.EnableNotifications,
                settings.PlaySound,
                settings.ShowBadge,
                settings.ShowPreview
            };
            await Auth.SetUserSettingAsync("notification-preferences", System.Text.Json.JsonSerializer.Serialize(local));
        }
        catch { }
    }

    private async Task LoadLocalPreferences()
    {
        try
        {
            var json = await Auth.GetUserSettingAsync("notification-preferences");
            if (!string.IsNullOrEmpty(json))
            {
                var local = System.Text.Json.JsonSerializer.Deserialize<LocalPreferences>(json);
                if (local is not null)
                {
                    settings.EnableNotifications = local.EnableNotifications;
                    settings.PlaySound = local.PlaySound;
                    settings.ShowBadge = local.ShowBadge;
                    settings.ShowPreview = local.ShowPreview;
                    // QuietDays dan AllowImportant sudah dari backend, tidak perlu di-override
                }
            }
        }
        catch { }
    }

    // Per-module sounds (wwwroot/js/sounds.js keeps them in localStorage "notification-sounds").
    internal record SoundModule(string Key, string Name, string Icon, string Color);
    internal static readonly SoundModule[] SoundModules =
    [
        new("dm", "Direct message", "message-circle", "#2e9d6b"),
        new("group", "Group chat", "messages-square", "#14a3a3"),
        new("mention", "Mention / emergency", "at-sign", "#e0662f"),
        new("mail", "New mail", "mail", "#2563eb"),
        new("meeting", "Meeting", "calendar-clock", "#7c4dd8"),
        new("project", "Project & task", "kanban-square", "#d4a10f"),
        new("announce", "News & memo", "megaphone", "#c2442e"),
        new("notes", "Notes", "notebook-pen", "#8a6d4b"),
        new("tickets", "Tickets", "life-buoy", "#0a84ff"),
        new("system", "System", "settings", "#6b6963"),
    ];
    private static readonly (string File, string Name)[] SoundChoices =
    [
        ("dm", "Bubble"), ("group", "Pebble"), ("mention", "Spark"), ("mail", "Envelope"), ("meeting", "Doorbell"),
        ("meeting-urgent", "Doorbell (loud)"), ("project", "Tasker"), ("announce", "Herald"), ("announce-firm", "Herald (firm)"),
        ("notes", "Paper"), ("tickets", "Lifeline"), ("system", "Ping"), ("x-glass", "Crystal"), ("x-drop", "Drop"), ("x-harp", "Harp"),
    ];
    internal int soundVolume = 80;
    internal Dictionary<string, string> soundMap = [];

    internal static List<AppOption> SoundOptions(string module) =>
        [.. SoundChoices.Select(c => new AppOption(c.File, c.File == module ? $"{c.Name} (default)" : c.Name)), new("", "No sound")];

    private async Task PickSound(string module, string? file)
    {
        soundMap[module] = file ?? "";
        await PreviewSound(soundMap[module]);
    }

    private async Task PreviewSound(string file)
    {
        if (file == "") return;
        try { await JS.InvokeVoidAsync("ipopSound.preview", file, soundVolume); } catch { }
    }

    private async Task LoadSoundPrefs()
    {
        try
        {
            var p = await JS.InvokeAsync<SoundPrefs>("ipopSound.getPrefs");
            soundVolume = p.Volume;
            soundMap = p.Map ?? [];
        }
        catch { }
    }

    private async Task SaveSoundPrefs()
    {
        try { await JS.InvokeVoidAsync("ipopSound.setPrefs", soundVolume, soundMap); } catch { }
        try { await Push.SyncPreferencesAsync(settings.PlaySound, soundVolume / 100.0); } catch { }
    }

    private record SoundPrefs(int Volume, Dictionary<string, string>? Map);

    private class LocalPreferences
    {
        public bool EnableNotifications { get; set; } = true;
        public bool PlaySound { get; set; } = true;
        public bool ShowBadge { get; set; } = true;
        public bool ShowPreview { get; set; } = true;
    }

    internal class NotificationPreferences
    {
        public bool EnableNotifications { get; set; } = true;
        public bool PlaySound { get; set; } = true;
        public bool ShowBadge { get; set; } = true;
        public bool ShowPreview { get; set; } = true;
        public bool QuietHoursEnabled { get; set; }
        public TimeOnly? QuietStart { get; set; } = new TimeOnly(22, 0);
        public TimeOnly? QuietEnd { get; set; } = new TimeOnly(7, 0);
        public List<string> QuietDays { get; set; } = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
        public bool AllowImportant { get; set; } = true;
        public List<string> EnabledTypes { get; set; } = NotificationCategories.All.Select(c => c.Id).ToList();
    }

    internal record NotificationType(string Id, string Name, string Description, string Icon);
    private record NotificationPreferenceDto(bool QuietHoursEnabled, string? QuietStart, string? QuietEnd, string? MutedTypes, string? QuietDays, bool AllowImportant);
    private List<string> notifierPcs = [];
    private sealed record NotifierMe(bool IctTools, List<string>? OnlinePcs);
}
