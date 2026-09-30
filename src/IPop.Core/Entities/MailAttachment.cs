namespace IPop.Core.Entities;

/// Attachment linked to a <see cref="Mail"/>. Files are stored on the NAS via the Synology File
/// service; this row only records the reference path (same seam Chat uses for message attachments).
public class MailAttachment
{
    public Guid Id { get; set; }
    /// Null while the upload is pending (composer attached it, no send/draft has linked it yet).
    public Guid? MailId { get; set; }
    /// Who uploaded the file. Only this user may link, delete or download it while it is pending.
    /// Null for rows created before the column existed (the path check is the fallback there).
    public Guid? UploaderId { get; set; }
    public required string FileName { get; set; }
    public long FileSize { get; set; }
    public required string MimeType { get; set; }
    public required string SynologyServer { get; set; }
    public required string SynologyPath { get; set; }
    public string? ThumbnailPath { get; set; }
    /// Urutan tampil lampiran (Fase 2 — panel kelola lampiran bisa mengurutkan).
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }

    public Mail? Mail { get; set; }
}
