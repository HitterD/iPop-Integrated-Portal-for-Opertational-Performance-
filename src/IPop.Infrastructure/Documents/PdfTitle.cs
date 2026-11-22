using DevExpress.Pdf;

namespace IPop.Infrastructure.Documents;

/// Browser PDF viewers show the document's Title metadata as the viewer/tab title and fall back to the URL's last
/// segment, which for a blob: URL is a random id (grilled 2026-11-20). Converted and rendered PDFs get a real title.
public static class PdfTitle
{
    public static byte[] Set(byte[] pdf, string? title)
    {
        if (pdf.Length == 0 || string.IsNullOrWhiteSpace(title)) return pdf;
        try
        {
            using var processor = new PdfDocumentProcessor();
            using (var input = new MemoryStream(pdf, writable: false)) processor.LoadDocument(input, true);
            processor.Document.Title = title.Trim();
            using var output = new MemoryStream();
            processor.SaveDocument(output);
            return output.ToArray();
        }
        catch
        {
            return pdf; // a title is cosmetic: never fail the preview over it
        }
    }
}
