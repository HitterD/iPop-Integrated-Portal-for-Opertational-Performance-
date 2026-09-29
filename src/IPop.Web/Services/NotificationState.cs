using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using IPop.Application.Notifications;

namespace IPop.Web.Services;

/// <summary>
/// Model notifikasi yang digunakan oleh shell (TopBar Bell Popover & Floating Banner).
/// </summary>
public record ShellNotificationItem(
    Guid Id,
    string Type,
    string Title,
    string Body,
    string? Source,
    DateTime At,
    bool IsRead,
    string Priority = "normal",
    string? TargetUrl = null,
        DateTime? SnoozedUntil = null);

/// <summary>
/// Model banner melayang bergaya macOS yang aktif di layar.
/// </summary>
public class ShellNotificationBanner
{
    public Guid Id { get; }
    public ShellNotificationItem Item { get; }
    public DateTime CreatedAt { get; }
    public bool IsPaused { get; set; }
    public CancellationTokenSource Cts { get; } = new();

    public ShellNotificationBanner(Guid id, ShellNotificationItem item)
    {
        Id = id;
        Item = item;
        CreatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Client-side notification state for the shell — the unread count behind the bell
/// badge in MainLayout, live floating banners, and recent notifications feed.
/// Connects to /hubs/notification and reacts to "notificationReceived" pushes.
/// </summary>
public class NotificationState : IDisposable
{
    private readonly AuthState _auth;
    private readonly HttpClient _http;
    private readonly ToastService? _toasts;
    private readonly IJSRuntime? _js;
    private HubConnection? _hub;

    private readonly List<ShellNotificationItem> _recentNotifications = [];
    public IReadOnlyList<ShellNotificationItem> RecentNotifications => _recentNotifications;

    private readonly List<ShellNotificationBanner> _activeBanners = [];
    public IReadOnlyList<ShellNotificationBanner> ActiveBanners => _activeBanners;

    public int UnreadCount { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    public event Action? UnreadChanged;
    public event Action? NotificationsChanged;
    public event Action? BannersChanged;
    /// Di-raise setiap LoadPreferencesAsync selesai — komponen shell (MainLayout)
    /// memuat ulang preferensi lokal (badge) tanpa menunggu remount.
    public event Action? PreferencesChanged;

    public NotificationState(AuthState auth, HttpClient http, ToastService toasts, IJSRuntime js)
    {
        _auth = auth;
        _http = http;
        _toasts = toasts;
        _js = js;
    }

    public async Task ConnectAsync()
    {
        if (_hub != null) return;
        if (!_auth.IsAuthenticated) return;

        var baseUrl = _http.BaseAddress!;
        var hubUrl = new Uri(baseUrl, "hubs/notification");

        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => o.AccessTokenProvider = () => Task.FromResult(_auth.AccessToken))
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();

        _hub.On<NotificationDeliveryState>("notificationReceived", OnNotificationReceived);

        _hub.Reconnecting += _ => Task.CompletedTask;
        _hub.Reconnected += _ => Task.CompletedTask;
        _hub.Closed += _ => Task.CompletedTask;

        try
        {
            await _hub.StartAsync();
            await LoadPreferencesAsync();
        }
        catch
        {
            await DisposeHubAsync();
        }
    }

    public async Task LoadRecentAsync(int limit = 6)
    {
        if (!_auth.IsAuthenticated) return;
        IsLoading = true;
        NotificationsChanged?.Invoke();
        try
        {
            var res = await _auth.GetAsync<InboxListResultDto>($"/api/notifications/v1/inbox?page=1&pageSize={limit}");
            if (res != null)
            {
                _recentNotifications.Clear();
                foreach (var item in res.Items)
                {
                    _recentNotifications.Add(new ShellNotificationItem(
                        item.Id,
                        item.Type,
                        item.Title,
                        item.Body,
                        item.Source,
                        item.At.Kind == DateTimeKind.Utc ? item.At.ToLocalTime() : item.At,
                        item.IsRead,
                        item.Priority ?? "normal",
                        item.TargetUrl));
                }
                SetUnread(res.UnreadCount);
            }
        }
        catch
        {
            // best-effort
        }
        finally
        {
            IsLoading = false;
            NotificationsChanged?.Invoke();
        }
    }

    private void OnNotificationReceived(NotificationDeliveryState delivery)
    {
        // Bump unread count
        SetUnread(UnreadCount + 1);

        var type = ClassifyEventType(delivery.EventType ?? "system");
        var item = new ShellNotificationItem(
            delivery.RequestId,
            type,
            string.IsNullOrWhiteSpace(delivery.Title) ? "New Notification" : delivery.Title,
            delivery.Body ?? "You received a new notification.",
            delivery.Channel,
            DateTime.Now,
            false,
            delivery.Priority ?? "normal",
            delivery.TargetUrl);

        // Prepend to recent list
        _recentNotifications.RemoveAll(x => x.Id == item.Id);
        _recentNotifications.Insert(0, item);
        if (_recentNotifications.Count > 10)
        {
            _recentNotifications.RemoveAt(_recentNotifications.Count - 1);
        }
        NotificationsChanged?.Invoke();

        // Add to floating banners queue (max 3 concurrent) — kecuali preferensi membisukan.
        // Prioritas diteruskan agar AllowImportant bisa menembus jam senyap untuk "high".
        var suppress = ShouldSuppress(item.Type, item.Priority);
        if (!suppress) AddBanner(item);

        // Toast interaktif (Fase 4): kartu elegan yang bisa diklik menuju sumber.
        if (!suppress)
        {
            _toasts?.ShowDetailed(
                item.Title,
                item.Body,
                ToastKind.Info,
                GetTypeIcon(item.Type),
                string.IsNullOrWhiteSpace(item.TargetUrl) ? null : item.TargetUrl,
                item.Id.ToString());
        }
    }

    // ── Preferensi notifikasi (Fase 4) — jam senyap + jenis dibisukan. ──
    // Dimuat dari server; menghormati jam senyap (rentang lintas tengah malam didukung),
    // hari aktif (QuietDays), pengecualian prioritas tinggi (AllowImportant),
    // dan daftar jenis yang dibisukan. Gagal memuat = tidak menekan apa pun.
    private NotificationPrefs _prefs = NotificationPrefs.Empty;

    public async Task LoadPreferencesAsync()
    {
        try
        {
            var p = await _auth.GetAsync<NotificationPrefsDto>("/api/notifications/v1/preferences");
            if (p is not null) _prefs = NotificationPrefs.From(p);
        }
        catch { /* preferensi opsional — lanjut tanpa penekanan */ }

        // Local-only prefs (EnableNotifications, ShowBadge, PlaySound, ShowPreview) live in localStorage.
        if (_js is not null)
        {
            try
            {
                var json = await _js.InvokeAsync<string>("localStorage.getItem", "notification-preferences");
                if (!string.IsNullOrEmpty(json))
                {
                    var local = System.Text.Json.JsonSerializer.Deserialize<LocalNotifPrefs>(json);
                    if (local is not null)
                        _prefs = _prefs with
                        {
                            EnableNotifications = local.EnableNotifications,
                            ShowBadge = local.ShowBadge,
                            ShowPreview = local.ShowPreview,
                            PlaySound = local.PlaySound
                        };
                }
            }
            catch { /* localStorage tidak tersedia — pakai default server */ }
        }

        // Notifikasi dinonaktifkan → bersihkan banner yang sedang tampil agar tidak
        // tersisa di layar sampai timer-nya habis.
        if (!_prefs.EnableNotifications && _activeBanners.Count > 0)
        {
            foreach (var b in _activeBanners.ToList())
            {
                try { b.Cts.Cancel(); b.Cts.Dispose(); } catch { }
            }
            _activeBanners.Clear();
            BannersChanged?.Invoke();
        }

        PreferencesChanged?.Invoke();
    }

    /// Prefs efektif saat ini (untuk MainLayout/ToastHost membaca ShowBadge dsb. tanpa menunggu reload).
    public bool CurrentShowBadge => _prefs.ShowBadge;
    public bool CurrentEnableNotifications => _prefs.EnableNotifications;
    public bool CurrentShowPreview => _prefs.ShowPreview;
    public bool CurrentPlaySound => _prefs.PlaySound;

    private bool ShouldSuppress(string type, string? priority = null)
    {
        if (!_prefs.EnableNotifications) return true; // notifikasi dinonaktifkan sepenuhnya
        if (_prefs.MutedTypes.Contains(type, StringComparer.OrdinalIgnoreCase)) return true;
        return IsInQuietHours(_prefs, priority);
    }

    /// <summary>
    /// Evaluasi jam senyap di sisi klien, konsisten dengan backend:
    /// zona waktu efektif dari server (profil ?? "SE Asia Standard Time"), QuietDays,
    /// rentang lintas tengah malam, dan pengecualian AllowImportant untuk prioritas "high".
    /// Bila ID zona tidak dikenali runtime ini, jam senyap TIDAK diterapkan di klien
    /// (fail-open) — backend tetap menjadi penekan otoritatif, sehingga keduanya tidak
    /// pernah berbeda arah (klien menekan sementara server tidak).
    /// </summary>
    internal static bool IsInQuietHours(NotificationPrefs prefs, string? priority = null)
        => IsInQuietHoursAt(prefs, DateTime.UtcNow, priority);

    /// Varian dengan waktu yang dapat diinjeksikan — membuat pengujian deterministik
    /// (tidak bergantung pada jam/zona mesin pengujian).
    internal static bool IsInQuietHoursAt(NotificationPrefs prefs, DateTime utcNow, string? priority = null)
    {
        if (!prefs.QuietHoursEnabled || prefs.QuietStart is not { } qs || prefs.QuietEnd is not { } qe)
            return false;

        DateTime now;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(prefs.TimeZoneId ?? "SE Asia Standard Time");
            now = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        }
        catch { return false; } // fail-open: biarkan backend yang menekan
        var current = TimeOnly.FromDateTime(now);

        // QuietDays: bila diisi, jam senyap hanya berlaku pada hari-hari tersebut.
        if (prefs.QuietDays.Count > 0)
        {
            var todayShort = now.DayOfWeek.ToString().ToLowerInvariant()[..3]; // "mon".."sun"
            if (!prefs.QuietDays.Contains(todayShort)) return false;
        }

        // Rentang lintas tengah malam (22:00–06:00) vs rentang normal (13:00–17:00).
        var inQuiet = qs <= qe
            ? current >= qs && current < qe
            : current >= qs || current < qe;
        // AllowImportant=true → notifikasi high priority tetap tampil walau jam senyap.
        return inQuiet && !(prefs.AllowImportant && string.Equals(priority, "high", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record LocalNotifPrefs(bool EnableNotifications = true, bool ShowBadge = true, bool ShowPreview = true, bool PlaySound = true);
    internal sealed record NotificationPrefsDto(bool QuietHoursEnabled, string? QuietStart, string? QuietEnd, string? MutedTypes, string? QuietDays = null, bool AllowImportant = true, string? TimeZoneId = null);
    internal sealed record NotificationPrefs(
        bool QuietHoursEnabled, TimeOnly? QuietStart, TimeOnly? QuietEnd,
        HashSet<string> MutedTypes, HashSet<string> QuietDays,
        bool EnableNotifications, bool AllowImportant, bool ShowBadge, bool ShowPreview, bool PlaySound,
        string? TimeZoneId)
    {
        public static readonly NotificationPrefs Empty = new(false, null, null, [], [], true, true, true, true, true, null);
        public static NotificationPrefs From(NotificationPrefsDto d) => new(
            d.QuietHoursEnabled,
            TimeOnly.TryParse(d.QuietStart, out var qs) ? qs : null,
            TimeOnly.TryParse(d.QuietEnd, out var qe) ? qe : null,
            (d.MutedTypes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase),
            (d.QuietDays ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase),
            true,
            d.AllowImportant,
            true,
            true,
            true,
            d.TimeZoneId);
    }

    private void AddBanner(ShellNotificationItem item)
    {
        while (_activeBanners.Count >= 3)
        {
            var oldest = _activeBanners[0];
            DismissBanner(oldest.Id);
        }

        var banner = new ShellNotificationBanner(item.Id, item);
        _activeBanners.Add(banner);
        BannersChanged?.Invoke();

        _ = StartBannerTimer(banner, 5000);
    }

    private async Task StartBannerTimer(ShellNotificationBanner banner, int durationMs)
    {
        try
        {
            await Task.Delay(durationMs, banner.Cts.Token);
            while (banner.IsPaused && !banner.Cts.Token.IsCancellationRequested)
            {
                await Task.Delay(500, banner.Cts.Token);
            }
            if (!banner.Cts.Token.IsCancellationRequested)
            {
                await FlyToBell(banner.Id);
                DismissBanner(banner.Id);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// Timed-out banners fly into the topbar bell before they're removed (js/notif-fly.js).
    private async Task FlyToBell(Guid id)
    {
        if (_js is null) return;
        try
        {
            await _js.InvokeAsync<bool>("ipopNotifFly.fly", id.ToString()).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { }
    }

    public void PauseBanner(Guid id)
    {
        var banner = _activeBanners.FirstOrDefault(b => b.Id == id);
        if (banner != null)
        {
            banner.IsPaused = true;
        }
    }

    public void ResumeBanner(Guid id)
    {
        var banner = _activeBanners.FirstOrDefault(b => b.Id == id);
        if (banner != null)
        {
            banner.IsPaused = false;
        }
    }

    public void DismissBanner(Guid id)
    {
        var banner = _activeBanners.FirstOrDefault(b => b.Id == id);
        if (banner != null)
        {
            try
            {
                banner.Cts.Cancel();
                banner.Cts.Dispose();
            }
            catch { }
            _activeBanners.Remove(banner);
            BannersChanged?.Invoke();
        }
    }

    public async Task MarkReadAsync(Guid requestId)
    {
        var idx = _recentNotifications.FindIndex(n => n.Id == requestId);
        if (idx >= 0 && !_recentNotifications[idx].IsRead)
        {
            _recentNotifications[idx] = _recentNotifications[idx] with { IsRead = true };
            SetUnread(Math.Max(0, UnreadCount - 1));
            NotificationsChanged?.Invoke();
        }

        DismissBanner(requestId);

        try
        {
            await _auth.SendAsync(HttpMethod.Post, $"/api/notifications/v1/notifications/{requestId}/read", new { });
        }
        catch
        {
            // best-effort
        }
    }

    public async Task MarkAllReadAsync()
    {
        if (UnreadCount == 0 && _recentNotifications.All(n => n.IsRead)) return;

        for (int i = 0; i < _recentNotifications.Count; i++)
        {
            _recentNotifications[i] = _recentNotifications[i] with { IsRead = true };
        }
        SetUnread(0);
        NotificationsChanged?.Invoke();

        foreach (var b in _activeBanners.ToList())
        {
            DismissBanner(b.Id);
        }

        try
        {
            await _auth.SendAsync(HttpMethod.Post, "/api/notifications/v1/inbox/read-all", new { });
        }
        catch
        {
            // best-effort
        }
    }

    public void SetUnread(int unread)
    {
        if (UnreadCount == unread) return;
        UnreadCount = unread;
        UnreadChanged?.Invoke();
    }

    public static string GetRedirectUrl(ShellNotificationItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.TargetUrl))
        {
            return item.TargetUrl;
        }

        return GetRedirectUrl(item.Type);
    }

    public static string GetRedirectUrl(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "chat" => "/chat",
            "mail" => "/mail",
            "project" => "/projects",
            "news" => "/news",
            _ => "/notifications/inbox"
        };
    }

    public static string GetTypeIcon(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "chat" => "message-circle",
            "mail" => "mail",
            "news" => "newspaper",
            "project" => "kanban",
            _ => "bell"
        };
    }

    public static string GetTypeLabel(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "chat" => "Chat",
            "mail" => "Mail",
            "news" => "News & Memos",
            "project" => "Projects",
            _ => "System"
        };
    }

    public static string ClassifyEventType(string eventType)
    {
        var e = eventType.ToLowerInvariant();
        if (e.StartsWith("chat.") || e == "chat") return "chat";
        if (e.StartsWith("mail.") || e == "mail") return "mail";
        if (e.StartsWith("news.") || e.StartsWith("memo.") || e == "news") return "news";
        if (e.StartsWith("project.") || e.StartsWith("task.") || e == "project") return "project";
        return "system";
    }

    private async Task DisposeHubAsync()
    {
        if (_hub is null) return;
        try { await _hub.DisposeAsync(); } catch { }
        _hub = null;
    }

    public void Dispose()
    {
        foreach (var b in _activeBanners.ToList())
        {
            try { b.Cts.Cancel(); b.Cts.Dispose(); } catch { }
        }
        _activeBanners.Clear();
        _ = DisposeHubAsync();
    }

    private record InboxListResultDto(
        IReadOnlyList<InboxItemRowDto> Items,
        int TotalCount,
        int UnreadCount,
        int Page,
        int PageSize);

    private record InboxItemRowDto(
        Guid Id,
        string Type,
        string Title,
        string Body,
        string Source,
        DateTime At,
        bool IsRead,
        string Priority,
        string? TargetUrl = null,
        DateTime? SnoozedUntil = null);
}
