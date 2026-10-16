namespace IPop.Web.Components.Phone.Mail;

/// One folder row in the phone Mailboxes sheet (M2). Accent = the blue unread pill (Inbox).
public sealed record MailboxItem(string Key, string Label, string Icon, int Count, bool Accent, bool Active);
