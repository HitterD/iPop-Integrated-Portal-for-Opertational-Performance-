namespace IPop.Core.Entities;

/// Kontak favorit per pengguna (Kontak Prioritas): pegawai yang ditandai bintang
/// agar selalu muncul di urutan atas direktori. Privat per pengguna.
public class UserFavoriteContact
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// NIK pegawai yang difavoritkan (bukan FK — pegawai bisa datang/pergi dari HRIS).
    public required string ContactNik { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = default!;
}
