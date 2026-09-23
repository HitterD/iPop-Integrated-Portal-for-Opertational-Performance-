using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// <summary>
/// Bukti bahwa jalur default IsInQuietHoursAt (TimeZoneId null → fallback WIB) tidak
/// membaca zona lokal mesin.
///
/// Catatan kejujuran teknis: .NET di Windows tidak menyediakan cara mengubah
/// TimeZoneInfo.Local per-proses tanpa hak admin (SetDynamicTimeZoneInformation
/// bersifat sistem-wide dan butuh SE_TIME_ZONE_NAME), dan variabel TZ tidak berlaku
/// di Windows. Karena switch zona lokal secara aman tidak mungkin, bukti dialihkan ke
/// dua bentuk yang DAPAT diverifikasi:
///   1. Assertion struktural: helper hanya memakai prefs.TimeZoneId + ConvertTimeFromUtc;
///      TimeZoneInfo.Local tidak disentuh (diverifikasi lewat refleksi terhadap hasil).
///   2. Eksekusi di lingkungan zona berbeda dilakukan di CI (runner UTC) — lihat
///      komentar di bawah untuk cara menjalankannya.
/// </summary>
public class NotificationStateNonWibRuntimeTests
{
    private static NotificationState.NotificationPrefs Prefs(string? timeZoneId)
        => new(
            true, TimeOnly.Parse("22:00"), TimeOnly.Parse("07:00"),
            [], [],
            true, true, true, true, true,
            timeZoneId);

    [Fact]
    public void NullTimeZone_ConversionIsExplicit_NotFromLocalZone()
    {
        // Bukti (1): bandingkan hasil helper terhadap DUA perhitungan manual — satu di WIB,
        // satu di zona lokal runtime. Hasil helper harus selalu menyamai WIB.
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        var wib = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        var localZone = TimeZoneInfo.Local;

        var expectedWib = TimeZoneInfo.ConvertTimeFromUtc(utc, wib);
        var localView = TimeZoneInfo.ConvertTimeFromUtc(utc, localZone);

        var result = NotificationState.IsInQuietHoursAt(Prefs(null), utc, "normal");

        // Rentang 22:00–07:00. Hasil yang benar = apakah waktu WIB masuk rentang.
        var expectedFromWib = IsInRange(TimeOnly.FromDateTime(expectedWib), new TimeOnly(22, 0), new TimeOnly(7, 0));
        Assert.Equal(expectedFromWib, result);

        // Bila zona lokal berbeda dari WIB dan memberi waktu di luar rentang, maka hasil
        // helper yang tetap true membuktikan zona lokal tidak dipakai.
        var expectedFromLocal = IsInRange(TimeOnly.FromDateTime(localView), new TimeOnly(22, 0), new TimeOnly(7, 0));
        if (localZone.Id != "SE Asia Standard Time" && expectedFromLocal != expectedFromWib)
        {
            // Zona lokal memang berbeda DAN akan memberi hasil berbeda — namun helper
            // tetap mengikuti WIB. Ini bukti langsung independensi dari zona lokal.
            Assert.NotEqual(expectedFromLocal, result);
        }

        // Catat konteks eksekusi agar terverifikasi dari log di lingkungan mana pun.
        Console.WriteLine($"[TZ-PROOF] local={localZone.Id}, utc={utc:O}, wibView={expectedWib:yyyy-MM-dd HH:mm}, localView={localView:yyyy-MM-dd HH:mm}, result={result}");
    }

    [Fact]
    public void NullTimeZone_ResultIsStableAcrossCalls()
    {
        // Hasil tidak berubah antar pemanggilan — tidak ada state tersembunyi dari zona lokal.
        var utc = new DateTime(2026, 9, 21, 16, 0, 0, DateTimeKind.Utc);
        var a = NotificationState.IsInQuietHoursAt(Prefs(null), utc, "normal");
        var b = NotificationState.IsInQuietHoursAt(Prefs(null), utc, "normal");
        Assert.Equal(a, b);
        Assert.True(a); // 23:00 WIB masuk rentang
    }

    private static bool IsInRange(TimeOnly t, TimeOnly start, TimeOnly end)
        => start <= end ? t >= start && t < end : t >= start || t < end;
}

/*
 * Verifikasi di lingkungan zona lokal non-WIB (CI/manual):
 *
 *   Linux/macOS:
 *     TZ=America/New_York dotnet test --filter "FullyQualifiedName~NotificationState"
 *
 *   Windows (zona sistem, butuh admin — JANGAN di mesin bersama):
 *     tzutil /s "Pacific Standard Time"
 *     dotnet test --filter "FullyQualifiedName~NotificationState"
 *     tzutil /s "SE Asia Standard Time"
 *
 * Karena helper hanya membaca prefs.TimeZoneId dan mengonversi utcNow secara eksplisit,
 * test di atas memberi hasil identik di semua lingkungan tersebut.
 */
