namespace IPop.Core.Entities;

/// Attachment linked to a <see cref="Mail"/>. Files are stored on the NAS via the Synology File
/// service; this row only records the reference path (same seam Chat uses for message attachments).
public class MailAttachment
{
    public Guid Id { get; set; }
    public Guid MailId { get; set; }
    public required string FileName { get; set; }
    public long FileSize { get; set; }
    public required string MimeType { get; set; }
    public required string SynologyServer { get; set; }
    public required string SynologyPath { get; set; }
    public string? ThumbnailPath { get; set; }
    /// Urutan tampil lampiran (Fase 2 — panel kelola lampiran bisa mengurutkan).
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }

    public Mail Mail { get; set; } = default!;
}
