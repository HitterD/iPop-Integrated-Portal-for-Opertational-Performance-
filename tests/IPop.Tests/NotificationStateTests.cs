using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

public class NotificationStateTests
{
    [Theory]
    [InlineData("chat", "/chat")]
    [InlineData("CHAT", "/chat")]
    [InlineData("mail", "/mail")]
    [InlineData("project", "/projects")]
    [InlineData("news", "/news")]
    [InlineData("system", "/notifications/inbox")]
    [InlineData("unknown", "/notifications/inbox")]
    public void GetRedirectUrl_ReturnsExpectedModuleRoute(string type, string expectedUrl)
    {
        var url = NotificationState.GetRedirectUrl(type);
        Assert.Equal(expectedUrl, url);
    }

    [Fact]
    public void GetRedirectUrl_WithItem_PrioritizesTargetUrl()
    {
        var itemWithTarget = new ShellNotificationItem(
            Guid.NewGuid(), "project", "Task", "Body", "channel", DateTime.UtcNow, false, "normal", "/projects/abc-123");
        var itemWithoutTarget = new ShellNotificationItem(
            Guid.NewGuid(), "chat", "Chat", "Body", "channel", DateTime.UtcNow, false, "normal", null);

        Assert.Equal("/projects/abc-123", NotificationState.GetRedirectUrl(itemWithTarget));
        Assert.Equal("/chat", NotificationState.GetRedirectUrl(itemWithoutTarget));
    }

    [Theory]
    [InlineData("chat", "message-circle")]
    [InlineData("mail", "mail")]
    [InlineData("news", "newspaper")]
    [InlineData("project", "kanban")]
    [InlineData("system", "bell")]
    [InlineData("other", "bell")]
    public void GetTypeIcon_ReturnsExpectedLucideIcon(string type, string expectedIcon)
    {
        var icon = NotificationState.GetTypeIcon(type);
        Assert.Equal(expectedIcon, icon);
    }

    [Theory]
    [InlineData("chat.mention", "chat")]
    [InlineData("chat.emergency", "chat")]
    [InlineData("mail.received", "mail")]
    [InlineData("news.published", "news")]
    [InlineData("memo.acknowledged", "news")]
    [InlineData("project.created", "project")]
    [InlineData("task.assigned", "project")]
    [InlineData("hris.sync", "system")]
    public void ClassifyEventType_CategorizesToCorrectModule(string eventType, string expectedModule)
    {
        var result = NotificationState.ClassifyEventType(eventType);
        Assert.Equal(expectedModule, result);
    }

    [Theory]
    [InlineData("chat", "Chat")]
    [InlineData("mail", "Mail")]
    [InlineData("news", "News & Memos")]
    [InlineData("project", "Projects")]
    [InlineData("system", "System")]
    public void GetTypeLabel_ReturnsReadableLabel(string type, string expectedLabel)
    {
        var label = NotificationState.GetTypeLabel(type);
        Assert.Equal(expectedLabel, label);
    }

    // ── IsInQuietHours (Fase 4) — logika jam senyap klien, konsisten dengan backend. ──
    // Semua test menginjeksikan waktu UTC secara eksplisit agar deterministik di mesin mana pun.

    private static NotificationState.NotificationPrefs Prefs(
        bool quietEnabled = true, string start = "22:00", string end = "07:00",
        string[]? quietDays = null, bool allowImportant = true, string? timeZoneId = null)
        => new(
            quietEnabled,
            TimeOnly.Parse(start),
            TimeOnly.Parse(end),
            [],
            (quietDays ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            true, allowImportant, true, true, true,
            timeZoneId);

    [Fact]
    public void IsInQuietHours_Disabled_ReturnsFalse()
    {
        Assert.False(NotificationState.IsInQuietHoursAt(Prefs(quietEnabled: false), DateTime.UtcNow, "normal"));
    }

    [Fact]
    public void IsInQuietHours_InvalidTimezone_FailsOpen()
    {
        // ID zona yang tidak dikenal runtime harus membuat klien TIDAK menekan apa pun —
        // backend tetap mengevaluasi dengan zona efektif, sehingga tidak ada perbedaan arah.
        Assert.False(NotificationState.IsInQuietHoursAt(Prefs(timeZoneId: "Not/A_Real_Zone"), DateTime.UtcNow, "normal"));
    }

    [Fact]
    public void IsInQuietHours_InWindow_WIB_Suppresses()
    {
        // 2026-09-21 16:00 UTC = 23:00 WIB (Senin) — masuk rentang 22:00–07:00.
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        Assert.True(NotificationState.IsInQuietHoursAt(Prefs(timeZoneId: "SE Asia Standard Time"), utc, "normal"));
    }

    [Fact]
    public void IsInQuietHours_OutsideWindow_WIB_DoesNotSuppress()
    {
        // 2026-09-21 05:00 UTC = 12:00 WIB (Senin) — di luar rentang 22:00–07:00.
        var utc = new DateTime(2026, 9, 21, 5, 0, 0, DateTimeKind.Utc);
        Assert.False(NotificationState.IsInQuietHoursAt(Prefs(timeZoneId: "SE Asia Standard Time"), utc, "normal"));
    }

    [Fact]
    public void IsInQuietHours_HighPriority_WithAllowImportant_NotSuppressed()
    {
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc); // 23:00 WIB
        Assert.False(NotificationState.IsInQuietHoursAt(Prefs(timeZoneId: "SE Asia Standard Time", allowImportant: true), utc, "high"));
    }

    [Fact]
    public void IsInQuietHours_HighPriority_WithoutAllowImportant_Suppressed()
    {
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc); // 23:00 WIB
        Assert.True(NotificationState.IsInQuietHoursAt(Prefs(timeZoneId: "SE Asia Standard Time", allowImportant: false), utc, "high"));
    }

    [Fact]
    public void IsInQuietHours_QuietDays_DayNotIncluded_DoesNotSuppress()
    {
        // 2026-09-21 16:00 UTC = Senin 23:00 WIB; jam senyap hanya Selasa → tidak menekan.
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        Assert.False(NotificationState.IsInQuietHoursAt(
            Prefs(timeZoneId: "SE Asia Standard Time", quietDays: ["tue"]), utc, "normal"));
    }

    [Fact]
    public void IsInQuietHours_QuietDays_DayIncluded_Suppresses()
    {
        // Senin 23:00 WIB; jam senyap termasuk Senin → menekan.
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        Assert.True(NotificationState.IsInQuietHoursAt(
            Prefs(timeZoneId: "SE Asia Standard Time", quietDays: ["mon"], allowImportant: false), utc, "normal"));
    }

    [Fact]
    public void IsInQuietHours_SameUtcDifferentZone_DifferentOutcome()
    {
        // UTC yang SAMA dievaluasi pada dua zona berbeda harus memberi hasil berbeda —
        // ini membuktikan hasil mengikuti zona waktu, bukan mesin pengujian.
        // 2026-09-21 16:00 UTC = 23:00 WIB (masuk rentang 22:00–07:00) tetapi
        // 01:00 di Sydney... periksa rentang siang agar beda hasil terlihat jelas:
        // rentang 09:00–17:00 → 23:00 WIB di luar, 01:00 Sydney di luar juga.
        // Pakai rentang 13:00–17:00: 15:00 WIB (masuk) vs 02:00 Sydney (di luar).
        var utc = new DateTime(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc); // 15:00 WIB, 18:00 Sydney
        var inWib = NotificationState.IsInQuietHoursAt(
            Prefs(start: "13:00", end: "17:00", timeZoneId: "SE Asia Standard Time"), utc, "normal");
        var inSydney = NotificationState.IsInQuietHoursAt(
            Prefs(start: "13:00", end: "17:00", timeZoneId: "AUS Eastern Standard Time"), utc, "normal");
        Assert.True(inWib);      // 15:00 WIB masuk rentang 13:00–17:00
        Assert.False(inSydney);  // 18:00 Sydney di luar rentang 13:00–17:00
    }

    [Fact]
    public void IsInQuietHours_NullTimeZone_UsesDefaultWib_NotLocalZone()
    {
        // Profil tanpa zona (null) harus dievaluasi di WIB — fallback sama dengan backend —
        // BUKAN zona lokal mesin. UTC 2026-09-21 16:00 = 23:00 WIB (masuk rentang malam).
        // Bila fallback keliru memakai zona lokal (mis. UTC di CI), hasilnya akan berbeda.
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        Assert.True(NotificationState.IsInQuietHoursAt(
            Prefs(timeZoneId: null), utc, "normal"));
        // Dan waktu yang sama pagi WIB harus di luar rentang:
        var utcMorning = new DateTime(2026, 9, 21, 5, 0, 0, DateTimeKind.Utc); // 12:00 WIB
        Assert.False(NotificationState.IsInQuietHoursAt(
            Prefs(timeZoneId: null), utcMorning, "normal"));
    }
}
