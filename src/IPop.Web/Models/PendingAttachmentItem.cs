namespace IPop.Web.Models;

public record PendingAttachmentItem(Guid Id, string FileName, long FileSize, string MimeType, string? PreviewUrl)
{
    public IPop.Application.Chat.AttachmentDto AsDto() => new(Id, FileName, FileSize, MimeType, null);
}
