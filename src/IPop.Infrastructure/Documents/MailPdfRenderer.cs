using DevExpress.XtraRichEdit;
using DevExpress.XtraRichEdit.API.Native;
using IPop.Application.Documents;

namespace IPop.Infrastructure.Documents;

/// Renderer PDF surat internal (Fase 2): merakit satu surat (kop, pengirim,
/// penerima, isi HTML) menjadi dokumen dan mengekspornya sebagai PDF.
/// Memakai WordProcessor DevExpress — HTML dari RTE dimuat sebagai HtmlFormat.
public sealed class MailPdfRenderer(IDevExpressDocumentFactory factory)
{
    private readonly IDevExpressDocumentFactory _factory = factory;
    /// Rakit satu surat menjadi PDF. `logoBytes` opsional untuk kop perusahaan.
    public Task<byte[]> RenderMailAsync(MailPdfModel m, CancellationToken ct = default)
        => Task.Run(() => Render(m), ct);

    /// Gabungkan beberapa surat menjadi satu dokumen PDF (tiap surat mulai halaman baru).
    public Task<byte[]> RenderMergedAsync(IReadOnlyList<MailPdfModel> mails, CancellationToken ct = default)
        => Task.Run(() => RenderMerged(mails), ct);

    private byte[] Render(MailPdfModel m)
    {
        using var server = _factory.CreateWordProcessor();
        ApplyPageSetup(server.Document);
        BuildDocument(server.Document, m);
        AddPageNumbers(server.Document);
        using var ms = new MemoryStream();
        server.ExportToPdf(ms);
        return ms.ToArray();
    }

    private byte[] RenderMerged(IReadOnlyList<MailPdfModel> mails)
    {
        using var server = _factory.CreateWordProcessor();
        var doc = server.Document;
        ApplyPageSetup(doc);
        for (var i = 0; i < mails.Count; i++)
        {
            if (i > 0) doc.AppendSection();
            BuildDocument(doc, mails[i]);
        }
        AddPageNumbers(doc);
        using var ms = new MemoryStream();
        server.ExportToPdf(ms);
        return ms.ToArray();
    }

    // Konsistensi format: A4, margin seragam, font dasar Calibri.
    private static void ApplyPageSetup(Document doc)
    {
        foreach (Section section in doc.Sections)
        {
            section.Page.PaperKind = DevExpress.Drawing.Printing.DXPaperKind.A4;
            section.Page.Landscape = false;
            section.Margins.Left = section.Margins.Right = 57f;   // ~2 cm
            section.Margins.Top = section.Margins.Bottom = 57f;
        }
        var all = doc.BeginUpdateCharacters(doc.Range);
        all.FontName = "Calibri";
        doc.EndUpdateCharacters(all);
    }

    // Nomor halaman "Halaman X dari Y" di footer tiap section.
    private static void AddPageNumbers(Document doc)
    {
        foreach (Section section in doc.Sections)
        {
            var footer = section.BeginUpdateFooter();
            try
            {
                var p = footer.Paragraphs.Append();
                p.Alignment = ParagraphAlignment.Center;
                footer.AppendText("Page ");
                footer.Fields.Create(footer.AppendText("").End, "PAGE");
                footer.AppendText(" of ");
                footer.Fields.Create(footer.AppendText("").End, "NUMPAGES");
                var range = footer.Range;
                var props = footer.BeginUpdateCharacters(range);
                props.FontSize = 8;
                props.ForeColor = System.Drawing.Color.Gray;
                footer.EndUpdateCharacters(props);
            }
            finally
            {
                section.EndUpdateFooter(footer);
            }
        }
    }

    private void BuildDocument(Document doc, MailPdfModel m)
    {
        // ── Kop surat ──
        var headerRange = doc.AppendText("IPop — Internal Mail\r\n");
        var headerProps = doc.BeginUpdateCharacters(headerRange);
        headerProps.Bold = true;
        headerProps.FontSize = 9;
        headerProps.ForeColor = System.Drawing.Color.Gray;
        doc.EndUpdateCharacters(headerProps);

        // ── Subjek ──
        var subjRange = doc.AppendText(m.Subject + "\r\n");
        var subjProps = doc.BeginUpdateCharacters(subjRange);
        subjProps.Bold = true;
        subjProps.FontSize = 15;
        doc.EndUpdateCharacters(subjProps);

        // ── Meta pengirim/penerima/tanggal ──
        var sent = m.SentAt?.ToLocalTime().ToString("dddd, dd MMMM yyyy HH:mm") ?? "Not yet sent";
        var meta = $"From: {m.SenderName}\r\nTo: {m.Recipients}\r\nDate: {sent}\r\n";
        if (!string.IsNullOrWhiteSpace(m.Importance) && m.Importance == "high") meta += "Priority: High\r\n";
        var metaRange = doc.AppendText(meta + "\r\n");
        var metaProps = doc.BeginUpdateCharacters(metaRange);
        metaProps.FontSize = 10;
        doc.EndUpdateCharacters(metaProps);

    // ── Isi (HTML dari RTE) ──
        if (m.IsHtml && !string.IsNullOrWhiteSpace(m.Body))
        {
            // Muat HTML sebagai dokumen sementara lalu salin isinya, agar format
            // rich-text dari composer ikut ke PDF.
            using var bodyServer = _factory.CreateWordProcessor();
            // Offline only: the importer would otherwise fetch every remote image URL from the
            // server's network (SSRF). Stored NAS images were already inlined as data: URIs.
            var html = IPop.Infrastructure.Mail.MailHtml.ForOfflineRender(m.Body);
            bodyServer.LoadDocument(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)), DocumentFormat.Html);
            doc.AppendDocumentContent(bodyServer.Document.Range);
        }
        else
        {
            var bodyRange = doc.AppendText(m.Body ?? "");
            var bodyProps = doc.BeginUpdateCharacters(bodyRange);
            bodyProps.FontSize = 11;
            doc.EndUpdateCharacters(bodyProps);
        }

        // ── Tanda tangan / paraf ──
        if (!string.IsNullOrWhiteSpace(m.Signature))
        {
            doc.AppendText("\r\n");
            var sigRange = doc.AppendText(m.Signature);
            var sigProps = doc.BeginUpdateCharacters(sigRange);
            sigProps.Italic = true;
            sigProps.FontSize = 10;
            doc.EndUpdateCharacters(sigProps);
        }
    }

    /// Parameter render satu surat.
    public sealed record MailPdfModel(
        string Subject, string Body, bool IsHtml,
        string SenderName, string SenderNik, string Recipients,
        string? Signature, string? Importance, DateTime? SentAt);
}
