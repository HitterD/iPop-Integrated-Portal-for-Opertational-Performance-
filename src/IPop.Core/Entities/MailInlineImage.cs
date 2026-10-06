namespace IPop.Core.Entities;

/// An image pasted into a mail body, stored as an <see cref="Attachment"/> (area "mail") and
/// referenced from the body as <c>/api/attachments/{id}</c>. The link is what lets the mail's
/// participants download it (MailAttachmentPolicy); one image can sit in many mails (reply, forward).
public class MailInlineImage
{
    public Guid MailId { get; set; }
    public Guid AttachmentId { get; set; }
}
