namespace IPop.Core.Entities;

public class ChatAttachment
{
    public Guid Id { get; set; }
    public Guid? MessageId { get; set; }
    public required string FileName { get; set; }
    public long FileSize { get; set; }
    public required string MimeType { get; set; }
    public required string SynologyServer { get; set; }
    public required string SynologyPath { get; set; }
    public string? ThumbnailPath { get; set; }
    public DateTime CreatedAt { get; set; }

    public ChatMessage Message { get; set; } = default!;
}
