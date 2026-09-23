namespace IPop.Core.Entities;

/// Preferensi notifikasi per pengguna (Fase 4): jam senyap + jenis yang dibisukan.
/// Disimpan server-side agar berlaku lintas perangkat; toast/banner menghormatinya.
public class NotificationPreference
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// Jam senyap aktif.
    public bool QuietHoursEnabled { get; set; }
    /// Mulai jam senyap (HH:mm, waktu lokal pengguna).
    public string? QuietStart { get; set; }
    /// Selesai jam senyap (HH:mm).
    public string? QuietEnd { get; set; }

    /// Hari aktif jam senyap, dipisah koma (mis. "mon,tue,wed"). Kosong = semua hari.
    public string QuietDays { get; set; } = "";

    /// Izinkan notifikasi penting (high priority) tetap masuk saat jam senyap.
    public bool AllowImportant { get; set; } = true;

    /// Jenis notifikasi yang dibisukan, dipisah koma (mis. "chat,news"). Kosong = semua tampil.
    public string MutedTypes { get; set; } = "";

    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = default!;
}
