using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using IPop.Application.Notifications;
using IPop.Application.Realtime;

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

    private readonly IJSRuntime? _js;
    private HubConnection? _hub;
    private IDisposable? _changedSub;
    private CancellationTokenSource? _resyncCts;

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

        _js = js;
    }

    public async Task ConnectAsync()
    {
        if (_hub != null) return;
        if (!_auth.IsAuthenticated || _auth.IsSuspended) return;

        var baseUrl = _http.BaseAddress!;
        var hubUrl = new Uri(baseUrl, "hubs/notification");

        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => o.AccessTokenProvider = () => Task.FromResult(_auth.AccessToken))
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();

        _hub.On<NotificationDeliveryState>("notificationReceived", OnNotificationReceived);
        // Read/cleared in another tab of this user (app hub) → re-fetch the bell.
        _changedSub ??= _auth.On(RealtimeEvents.NotificationsChanged, ScheduleResync);

        var hub = _hub;
        _hub.Reconnecting += _ => Task.CompletedTask;
        // Anything pushed while disconnected was missed: re-seed count + recent list.
        _hub.Reconnected += _ => { ScheduleResync(); return Task.CompletedTask; };
        _hub.Closed += ex =>
        {
            _ = HubRetry.RunAsync(hub, () => _hub == hub && _auth.IsAuthenticated && !_auth.IsSuspended, ScheduleResync);
            return Task.CompletedTask;
        };

        try
        {
            await _hub.StartAsync();
            await LoadPreferencesAsync();
        }
        catch
        {
            _ = HubRetry.RunAsync(hub, () => _hub == hub && _auth.IsAuthenticated && !_auth.IsSuspended,
                () => { ScheduleResync(); _ = LoadPreferencesAsync(); });
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

    /// Debounced (300ms) re-fetch: a "mark all read" in another tab, or a reconnect burst,
    /// becomes one inbox request rather than several.
    private void ScheduleResync()
    {
        _resyncCts?.Cancel();
        var cts = _resyncCts = new CancellationTokenSource();
        _ = Task.Delay(300, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled || !_auth.IsAuthenticated) return;
            await LoadRecentAsync(Math.Max(6, _recentNotifications.Count));
        }, TaskScheduler.Default);
    }

    /// Set while practising: real notifications are counted by the practice bar, not shown.
    public Action? PracticeHold { get; set; }

    private void OnNotificationReceived(NotificationDeliveryState delivery)
    {
        if (PracticeHold is { } hold) { hold(); return; }
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
        // Busy: no banner or sound; the bell still counts it. Emergencies always break through.
        if (suppress || (_auth.IsBusy && !IsEmergency(delivery.EventType))) return;

        // The banner is the single on-screen surface for incoming notifications; toasts are
        // reserved for feedback on the user's own actions, so the two never duplicate.
        AddBanner(item);
        var (module, urgent) = SoundFor(delivery.EventType, item.Priority);
        PlaySound("ipopSound.playFor", module, urgent);
    }

    internal static bool IsEmergency(string? eventType) =>
        string.Equals(eventType, "chat.emergency", StringComparison.OrdinalIgnoreCase);

    /// Which module sound an event plays (js/sounds.js), and whether it is the louder variant.
    internal static (string Module, bool Urgent) SoundFor(string? eventType, string? priority)
    {
        var e = (eventType ?? "").ToLowerInvariant();
        var high = string.Equals(priority, "high", StringComparison.OrdinalIgnoreCase);
        if (e is "chat.mention" or "chat.emergency" || e.StartsWith("note.mention")) return ("mention", false);
        if (e.StartsWith("chat.")) return (e.Contains("group") ? "group" : "dm", false);
        if (e.StartsWith("mail.")) return ("mail", false);
        if (e.StartsWith("meeting.")) return ("meeting", e == "meeting.reminder");
        if (e.StartsWith("project.") || e.StartsWith("task.")) return ("project", false);
        if (e.StartsWith("news.") || e.StartsWith("memo."))
            return ("announce", e is "memo.mandatory" or "news.condolence" || (high && e.StartsWith("memo.")));
        if (e.StartsWith("note.")) return ("notes", false);
        if (e.StartsWith("ticket.")) return ("tickets", false);
        return ("system", false);
    }

    private void PlaySound(string fn, params object?[] args)
    {
        if (!_prefs.PlaySound || _js is null) return;
        _ = _js.InvokeVoidAsync(fn, args).AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
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

    // ── Incoming chat banners ──
    // Chat messages are banner-only (never the bell/inbox) and fold into one card per room:
    // a second message updates that card to "N new messages" instead of stacking another.
    // Mentions and emergencies are skipped here because the server already sends them as
    // chat.mention / chat.emergency notifications (and those break through room mute).
    public const string ChatMessageType = "chat.message";
    private readonly Dictionary<Guid, (Guid BannerId, int Count)> _chatBanners = [];
    private DateTime _lastChatSound = DateTime.MinValue;

    internal static bool ShouldBannerChat(IncomingChatMessage m, string currentPath)
        => !m.RoomMuted && !m.MentionsMe
           && m.Message.Type is not ("system" or "emergency")
           && !(m.IsActiveRoom && IsChatRoomPath(currentPath, m.Message.RoomId));

    /// The open conversation is only on screen while the route is /chat or /chat/{thatRoom}.
    internal static bool IsChatRoomPath(string path, Guid roomId)
    {
        var p = (path ?? "").Split('?', '#')[0].TrimEnd('/').ToLowerInvariant();
        return p == "/chat" || p == $"/chat/{roomId:D}";
    }

    public void ShowChatMessage(IncomingChatMessage m, string currentPath)
    {
        if (PracticeHold is not null) return;
        if (!ShouldBannerChat(m, currentPath))
        {
            // A message landing in the conversation on screen gets only a soft tick.
            if (IsTickChat(m, currentPath) && !ShouldSuppress("chat") && !_auth.IsBusy) PlaySound("ipopSound.play", "tick");
            return;
        }
        if (ShouldSuppress("chat") || _auth.IsBusy) return;

        var roomId = m.Message.RoomId;
        var count = 1;
        if (_chatBanners.TryGetValue(roomId, out var existing) && _activeBanners.Any(b => b.Id == existing.BannerId))
        {
            count = existing.Count + 1;
            DismissBanner(existing.BannerId);
        }

        var item = BuildChatItem(m, count);
        _chatBanners[roomId] = (item.Id, count);
        AddBanner(item);

        // One chime per burst, not per message.
        if (DateTime.UtcNow - _lastChatSound > TimeSpan.FromSeconds(3))
        {
            _lastChatSound = DateTime.UtcNow;
            PlaySound("ipopSound.playFor", m.Room is { Type: "group" } ? "group" : "dm", false);
        }
    }

    /// The message is for the open conversation the user is looking at (not muted, not a mention or
    /// emergency — those come as their own notification with the mention sound).
    internal static bool IsTickChat(IncomingChatMessage m, string currentPath)
        => !m.RoomMuted && !m.MentionsMe
           && m.Message.Type is not ("system" or "emergency")
           && m.IsActiveRoom && IsChatRoomPath(currentPath, m.Message.RoomId);

    internal static ShellNotificationItem BuildChatItem(IncomingChatMessage m, int count)
    {
        var msg = m.Message;
        var isGroup = m.Room is { Type: "group" };
        var name = isGroup
            ? (string.IsNullOrWhiteSpace(m.Room!.Name) ? "Group" : m.Room.Name!)
            : (m.Room?.RecipientName ?? msg.SenderName);
        var preview = IPop.Application.Chat.Mentions.MentionPlainText.Project(msg.Content ?? "", 120);
        if (string.IsNullOrWhiteSpace(preview))
            preview = msg.Attachments.Count > 0 ? "Sent an attachment" : "New message";
        var body = isGroup || m.Room is null ? $"{msg.SenderName}: {preview}" : preview;
        var title = count > 1 ? $"{name} · {count} new messages" : name;
        return new ShellNotificationItem(Guid.NewGuid(), ChatMessageType, title, body, "chat",
            DateTime.Now, true, "normal", $"/chat/{msg.RoomId:D}");
    }

    /// Opening a room means its messages are being read — drop that room's card.
    public void ClearChatBanner(Guid roomId)
    {
        if (_chatBanners.Remove(roomId, out var entry)) DismissBanner(entry.BannerId);
    }

    public static bool IsLocalOnly(ShellNotificationItem item) => item.Type == ChatMessageType;

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
            foreach (var room in _chatBanners.Where(kv => kv.Value.BannerId == id).Select(kv => kv.Key).ToList())
                _chatBanners.Remove(room);
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

    /// A module opened the item a notification points at (kind: chat | mail | calendar | meeting |
    /// task | project | news | note). The server marks matching notifications read; skip the
    /// round-trip when nothing is unread, and re-fetch the bell only when something changed.
    public async Task MarkReadByTargetAsync(string kind, Guid id)
    {
        if (!_auth.IsAuthenticated || id == Guid.Empty || UnreadCount == 0) return;
        try
        {
            var res = await _auth.SendAsync(HttpMethod.Post, "/api/notifications/v1/read-by-target", new { kind, id });
            if (!res.IsSuccessStatusCode) return;
            var body = await res.Content.ReadFromJsonAsync<ReadByTargetResult>();
            if (body?.Updated > 0) ScheduleResync();
        }
        catch
        {
            // best-effort
        }
    }

    private sealed record ReadByTargetResult(int Updated);

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

    public static string ClassifyEventType(string eventType)
    {
        var e = (eventType ?? "").ToLowerInvariant();
        if (e.StartsWith("chat.") || e == "chat") return "chat";
        if (e.StartsWith("mail.") || e == "mail") return "mail";
        if (e.StartsWith("news.") || e.StartsWith("memo.") || e == "news") return "news";
        if (e.StartsWith("project.") || e.StartsWith("task.") || e == "project") return "project";
        return "system";
    }

    public static (string Bg, string Color, string Icon) GetVisual(string type, string? title = null)
    {
        var e = (type ?? "").ToLowerInvariant();
        var t = (title ?? "").ToLowerInvariant();
        if (t.Contains("mention") || t.Contains("menyebut") || e.Contains("mention"))
            return ("#FFF3E0", "#FF9800", "at-sign");
        if (t.Contains("meeting") || t.Contains("rapat") || t.Contains("kalender") || e.Contains("meeting") || e.Contains("call"))
            return ("#E8F1FE", "#0A84FF", "calendar");
        if (e.StartsWith("chat.") || e == "chat" || t.Contains("message") || t.Contains("pesan") || t.Contains("chat"))
            return ("#E4F4EA", "#1C7A44", "message-circle");
        if (e.StartsWith("mail.") || e == "mail" || t.Contains("email") || t.Contains("surel") || t.Contains("mail"))
            return ("#E8F8F0", "#2E9E5B", "mail");
        if (e.StartsWith("news.") || e.StartsWith("memo.") || e == "news" || t.Contains("news") || t.Contains("memo") || t.Contains("berita"))
            return ("#FFF4E5", "#FF9F0A", "newspaper");
        if (e.StartsWith("project.") || e.StartsWith("task.") || e == "project" || t.Contains("project") || t.Contains("task") || t.Contains("tugas"))
            return ("#F2E7FE", "#7657C4", "kanban");
        return ("#F2F2F4", "#8E8E93", "bell");
    }

    public static string GetTypeIcon(string type, string? title = null) => GetVisual(type, title).Icon;

    public static (string Bg, string Color) GetTypeColors(string type, string? title = null)
    {
        var v = GetVisual(type, title);
        return (v.Bg, v.Color);
    }

    public static string GetTypeLabel(string type, string? title = null)
    {
        var icon = GetVisual(type, title).Icon;
        return icon switch
        {
            "at-sign" => "Mentions",
            "message-circle" => "Chat",
            "mail" => "Mail",
            "calendar" => "Meetings",
            "newspaper" => "News & Memos",
            "kanban" => "Projects",
            _ => "System"
        };
    }

    /// Session moved to another tab (SessionLockState); ConnectAsync resumes.
    public Task DisconnectAsync() => DisposeHubAsync();

    private async Task DisposeHubAsync()
    {
        _resyncCts?.Cancel();
        _changedSub?.Dispose(); _changedSub = null;
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
