using IPop.Application.Mail;

namespace IPop.Infrastructure.Documents;

/// Layanan render HTML pratinjau surat: merakit model surat menjadi HTML siap-tampil
/// untuk panel pratinjau (Fase 3 — Pratinjau & Ekspor Surat).
public sealed class MailHtmlRenderer
{
    /// Render surat sebagai HTML lengkap dengan styling internal.
    public string RenderMailHtml(MailHtmlModel model)
    {
        var html = new System.Text.StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"id\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"UTF-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        html.AppendLine($"<title>{Escape(model.Subject)}</title>");
        html.AppendLine("<style>");
        html.AppendLine(GetStyles());
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<div class=\"mail-container\">");

        // Header
        html.AppendLine("<header class=\"mail-header\">");
        html.AppendLine("<div class=\"mail-logo\">IPop</div>");
        html.AppendLine("<div class=\"mail-title\">Internal Mail</div>");
        html.AppendLine("</header>");

        // Meta
        html.AppendLine("<div class=\"mail-meta\">");
        html.AppendLine("<div class=\"meta-row\">");
        html.AppendLine("<span class=\"meta-label\">From:</span>");
        html.AppendLine($"<span class=\"meta-value\">{Escape(model.SenderName)} &lt;{Escape(model.SenderEmail)}&gt;</span>");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"meta-row\">");
        html.AppendLine("<span class=\"meta-label\">To:</span>");
        html.AppendLine($"<span class=\"meta-value\">{Escape(model.Recipients)}</span>");
        html.AppendLine("</div>");
        if (!string.IsNullOrWhiteSpace(model.Cc))
        {
            html.AppendLine("<div class=\"meta-row\">");
            html.AppendLine("<span class=\"meta-label\">Cc:</span>");
            html.AppendLine($"<span class=\"meta-value\">{Escape(model.Cc)}</span>");
            html.AppendLine("</div>");
        }
        html.AppendLine("<div class=\"meta-row\">");
        html.AppendLine("<span class=\"meta-label\">Date:</span>");
        html.AppendLine($"<span class=\"meta-value\">{model.SentAt?.ToString("dddd, dd MMMM yyyy HH:mm") ?? "Not yet sent"}</span>");
        html.AppendLine("</div>");
        if (!string.IsNullOrWhiteSpace(model.Importance) && model.Importance == "high")
        {
            html.AppendLine("<div class=\"meta-row priority-high\">");
            html.AppendLine("<span class=\"meta-label\">Priority:</span>");
            html.AppendLine("<span class=\"meta-value\">High</span>");
            html.AppendLine("</div>");
        }
        html.AppendLine("</div>");

        // Subject
        html.AppendLine($"<h1 class=\"mail-subject\">{Escape(model.Subject)}</h1>");

        // Body
        html.AppendLine("<div class=\"mail-body\">");
        if (model.IsHtml)
        {
            html.AppendLine(model.Body);
        }
        else
        {
            html.AppendLine($"<p>{Escape(model.Body).Replace("\n", "<br>")}</p>");
        }
        html.AppendLine("</div>");

        // Attachments
        if (model.Attachments.Count > 0)
        {
            html.AppendLine("<div class=\"mail-attachments\">");
            html.AppendLine("<div class=\"attachments-header\">");
            html.AppendLine("<strong>Attachments:</strong>");
            html.AppendLine("</div>");
            html.AppendLine("<ul class=\"attachments-list\">");
            foreach (var att in model.Attachments)
            {
                html.AppendLine($"<li>{Escape(att.FileName)} ({FormatFileSize(att.FileSize)})</li>");
            }
            html.AppendLine("</ul>");
            html.AppendLine("</div>");
        }

        // Signature
        if (!string.IsNullOrWhiteSpace(model.Signature))
        {
            html.AppendLine("<div class=\"mail-signature\">");
            html.AppendLine(model.Signature);
            html.AppendLine("</div>");
        }

        // Footer
        html.AppendLine("<footer class=\"mail-footer\">");
        html.AppendLine("<p>This email was sent via IPop — Company Internal Platform</p>");
        html.AppendLine("</footer>");

        html.AppendLine("</div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    private static string GetStyles() => @"
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            font-size: 14px;
            line-height: 1.6;
            color: #333;
            background: #f5f5f5;
        }
        .mail-container {
            max-width: 800px;
            margin: 0 auto;
            background: white;
            box-shadow: 0 2px 8px rgba(0,0,0,0.1);
        }
        .mail-header {
            background: linear-gradient(135deg, #2e7d5b 0%, #1e5c3f 100%);
            color: white;
            padding: 24px 32px;
            display: flex;
            align-items: center;
            gap: 16px;
        }
        .mail-logo {
            font-size: 28px;
            font-weight: 700;
        }
        .mail-title {
            font-size: 14px;
            opacity: 0.9;
        }
        .mail-meta {
            padding: 24px 32px;
            background: #f8f9fa;
            border-bottom: 1px solid #e9ecef;
        }
        .meta-row {
            display: flex;
            margin-bottom: 8px;
        }
        .meta-row:last-child {
            margin-bottom: 0;
        }
        .meta-label {
            font-weight: 600;
            color: #6c757d;
            min-width: 80px;
        }
        .meta-value {
            color: #212529;
        }
        .priority-high .meta-value {
            color: #dc3545;
            font-weight: 600;
        }
        .mail-subject {
            padding: 24px 32px;
            font-size: 20px;
            font-weight: 600;
            color: #212529;
            border-bottom: 2px solid #2e7d5b;
        }
        .mail-body {
            padding: 32px;
            min-height: 200px;
        }
        .mail-body img {
            max-width: 100%;
            height: auto;
            border-radius: 8px;
        }
        .mail-body p {
            margin-bottom: 16px;
        }
        .mail-attachments {
            padding: 24px 32px;
            background: #f8f9fa;
            border-top: 1px solid #e9ecef;
        }
        .attachments-header {
            margin-bottom: 12px;
            color: #495057;
        }
        .attachments-list {
            list-style: none;
            padding-left: 0;
        }
        .attachments-list li {
            padding: 8px 12px;
            background: white;
            border: 1px solid #dee2e6;
            border-radius: 6px;
            margin-bottom: 8px;
            display: flex;
            align-items: center;
            gap: 8px;
        }
        .attachments-list li:before {
            content: '📎';
        }
        .mail-signature {
            padding: 24px 32px;
            border-top: 1px solid #e9ecef;
            font-style: italic;
            color: #6c757d;
        }
        .mail-footer {
            padding: 24px 32px;
            text-align: center;
            font-size: 12px;
            color: #6c757d;
            background: #f8f9fa;
            border-top: 1px solid #e9ecef;
        }
        @media print {
            body { background: white; }
            .mail-container { box-shadow: none; max-width: 100%; }
            .mail-header { background: #2e7d5b !important; -webkit-print-color-adjust: exact; }
        }
    ";

    private static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.#} {sizes[order]}";
    }
}

/// Parameter render HTML satu surat.
public sealed record MailHtmlModel(
    string Subject,
    string Body,
    bool IsHtml,
    string SenderName,
    string SenderEmail,
    string Recipients,
    string? Cc,
    string? Signature,
    string? Importance,
    DateTime? SentAt,
    IReadOnlyList<MailHtmlAttachment> Attachments);

public sealed record MailHtmlAttachment(string FileName, long FileSize);
