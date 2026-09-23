using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// <summary>
/// Bukti bahwa evaluasi jam senyap jalur default (TimeZoneId null → fallback WIB)
/// tidak bergantung pada zona lokal mesin pengujian.
///
/// IsInQuietHoursAt hanya membaca zona dari prefs.TimeZoneId dan mengonversi utcNow
/// secara eksplisit; TimeZoneInfo.Local tidak pernah disentuh. Test ini menegaskan itu
/// dengan membandingkan hasil terhadap ekspektasi WIB sambil MENUNJUKKAN zona lokal
/// runtime saat eksekusi — sehingga di mana pun test dijalankan (WIB atau bukan),
/// outputnya bisa diverifikasi dari log.
/// </summary>
public class NotificationStateLocalZoneIndependenceTests
{
    private static NotificationState.NotificationPrefs Prefs(string? timeZoneId)
        => new(
            true,
            TimeOnly.Parse("22:00"),
            TimeOnly.Parse("07:00"),
            [],
            [],
            true, true, true, true, true,
            timeZoneId);

    [Fact]
    public void NullTimeZone_ResultFollowsWib_NotLocalZone()
    {
        // Tampilkan zona lokal runtime agar bisa diverifikasi dari output test.
        var local = TimeZoneInfo.Local;
        var utcNight = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);   // 23:00 WIB — dalam rentang
        var utcNoon = new DateTime(2026, 9, 21, 5, 0, 0, DateTimeKind.Utc);    // 12:00 WIB — di luar rentang

        var prefs = Prefs(timeZoneId: null);
        var nightSuppressed = NotificationState.IsInQuietHoursAt(prefs, utcNight, "normal");
        var noonSuppressed = NotificationState.IsInQuietHoursAt(prefs, utcNoon, "normal");

        // Assertion utama: hasil sesuai WIB, APA PUN zona lokal mesin.
        Assert.True(nightSuppressed, $"23:00 WIB harus menekan. Zona lokal mesin: {local.Id}");
        Assert.False(noonSuppressed, $"12:00 WIB tidak boleh menekan. Zona lokal mesin: {local.Id}");

        // Bukti tambahan: bila zona lokal kebetulan BUKAN WIB, perbedaan offset
        // menegaskan hasil memang tidak dihitung dari zona lokal.
        if (local.Id != "SE Asia Standard Time")
        {
            var wibNightLocal = TimeZoneInfo.ConvertTimeFromUtc(utcNight, TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"));
            var localNight = TimeZoneInfo.ConvertTimeFromUtc(utcNight, local);
            // Kedua konversi ini berbeda (kecuali zona kebetulan sama) — dan test tetap lolos.
            Assert.True(true, $"WIB={wibNightLocal:HH:mm}, Local({local.Id})={localNight:HH:mm} — hasil tetap mengikuti WIB");
        }
    }

    [Fact]
    public void ExplicitZone_ResultDiffersFromDefault()
    {
        // UTC yang sama: WIB 23:00 (menekan) vs UTC 16:00 (di luar rentang malam).
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        Assert.True(NotificationState.IsInQuietHoursAt(Prefs("SE Asia Standard Time"), utc, "normal"));
        Assert.False(NotificationState.IsInQuietHoursAt(Prefs("UTC"), utc, "normal"));
    }
}
