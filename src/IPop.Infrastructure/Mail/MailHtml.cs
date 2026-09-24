using Ganss.Xss;

namespace IPop.Infrastructure.Mail;

/// Mail bodies come from the rich-text composer and are rendered as raw markup by the
/// client, so every stored and served body goes through this allow-list first.
public static class MailHtml
{
    private static readonly HtmlSanitizer Sanitizer = Create();

    private static HtmlSanitizer Create()
    {
        var s = new HtmlSanitizer();
        s.AllowedSchemes.Add("mailto");
        s.AllowedAttributes.Add("class");
        s.AllowedAttributes.Add("data-nik");     // @mention chips
        s.AllowedAttributes.Add("data-project"); // linked-project chips
        return s;
    }

    public static string Sanitize(string? html) =>
        string.IsNullOrEmpty(html) ? "" : Sanitizer.Sanitize(html);
}
