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
}
