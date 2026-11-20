using DevExpress.Pdf;
using DevExpress.XtraRichEdit;
using IPop.Application.Documents;

namespace IPop.Infrastructure.Documents;

public class PdfService(IDevExpressDocumentFactory factory) : IPdfService
{
    public Task<byte[]> CreateSamplePdfAsync(string title, string message, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var server = factory.CreateWordProcessor();
            var doc = server.Document;

            var titleRange = doc.AppendText(title + "\r\n\r\n");
            var titleProps = doc.BeginUpdateCharacters(titleRange);
            titleProps.Bold = true;
            titleProps.FontSize = 16;
            doc.EndUpdateCharacters(titleProps);

            var bodyRange = doc.AppendText(message);
            var bodyProps = doc.BeginUpdateCharacters(bodyRange);
            bodyProps.FontSize = 11;
            doc.EndUpdateCharacters(bodyProps);

            using var pdfMs = new MemoryStream();
            server.ExportToPdf(pdfMs);
            return pdfMs.ToArray();
        }, ct);
    }

    public Task<byte[]> MergePdfAsync(IEnumerable<Stream> pdfStreams, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var processor = factory.CreatePdfProcessor();
            processor.CreateEmptyDocument();

            foreach (var stream in pdfStreams)
            {
                if (stream.CanSeek) stream.Position = 0;
                processor.AppendDocument(stream);
            }

            using var outMs = new MemoryStream();
            processor.SaveDocument(outMs);
            return outMs.ToArray();
        }, ct);
    }

    public Task<int> GetPageCountAsync(Stream pdfStream, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var processor = factory.CreatePdfProcessor();
            if (pdfStream.CanSeek) pdfStream.Position = 0;
            processor.LoadDocument(pdfStream, true);
            return processor.Document?.Pages.Count ?? 0;
        }, ct);
    }

    public Task<string> ExtractTextAsync(Stream pdfStream, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var processor = factory.CreatePdfProcessor();
            if (pdfStream.CanSeek) pdfStream.Position = 0;
            processor.LoadDocument(pdfStream, true);
            return processor.GetText() ?? string.Empty;
        }, ct);
    }

    public Task<PdfFirstPage> RenderFirstPageAsync(Stream pdfStream, int longestEdge, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var processor = factory.CreatePdfProcessor();
            if (pdfStream.CanSeek) pdfStream.Position = 0;
            processor.LoadDocument(pdfStream, true);
            var pages = processor.Document?.Pages.Count ?? 0;
            if (pages == 0) return new PdfFirstPage(0, null);
            ct.ThrowIfCancellationRequested();
            // DXBitmap draws through the Skia engine (DocumentServiceExtensions), so this also works in the Linux containers.
            using var bmp = processor.CreateDXBitmap(1, longestEdge);
            using var ms = new MemoryStream();
            bmp.Save(ms, DevExpress.Drawing.DXImageFormat.Jpeg);
            return new PdfFirstPage(pages, ms.Length > 0 ? ms.ToArray() : null);
        }, ct);
    }
}
