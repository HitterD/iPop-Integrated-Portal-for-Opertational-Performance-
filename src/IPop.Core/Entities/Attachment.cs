namespace IPop.Core.Entities;

/// Metadata for a file stored on the NAS by the service account (v6 foundation spec §6).
/// StoragePath is derived from Id, never from the user-supplied FileName.
public class Attachment
{
    public Guid Id { get; set; }
    public required string Area { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Sha256 { get; set; }
    public required string StoragePath { get; set; }
    public Guid UploaderId { get; set; }
    public User Uploader { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
