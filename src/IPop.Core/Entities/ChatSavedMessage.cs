namespace IPop.Core.Entities;

/// Simpanan pribadi: pesan yang disimpan pengguna untuk dirinya sendiri.
/// Berbeda dari ChatPinnedMessage (milik ruang, terlihat semua anggota) —
/// baris ini privat per pengguna dan tidak mengubah tampilan ruang.
public class ChatSavedMessage
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid MessageId { get; set; }
    public DateTime SavedAt { get; set; }

    public User User { get; set; } = default!;
    public ChatMessage Message { get; set; } = default!;
}
