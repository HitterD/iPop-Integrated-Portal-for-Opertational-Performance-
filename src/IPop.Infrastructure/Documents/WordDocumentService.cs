using DevExpress.XtraRichEdit;
using DevExpress.XtraRichEdit.API.Native;
using IPop.Application.Documents;

namespace IPop.Infrastructure.Documents;

public class WordDocumentService(IDevExpressDocumentFactory factory) : IWordDocumentService
{
    public Task<byte[]> CreateSampleDocumentAsync(string title, string content, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var server = factory.CreateWordProcessor();
            var doc = server.Document;

            var titleRange = doc.AppendText(title + "\r\n");
            var titleProps = doc.BeginUpdateCharacters(titleRange);
            titleProps.Bold = true;
            titleProps.FontSize = 18;
            doc.EndUpdateCharacters(titleProps);

            var bodyRange = doc.AppendText(content);
            var bodyProps = doc.BeginUpdateCharacters(bodyRange);
            bodyProps.FontSize = 11;
            doc.EndUpdateCharacters(bodyProps);

            using var ms = new MemoryStream();
            server.SaveDocument(ms, DocumentFormat.OpenXml);
            return ms.ToArray();
        }, ct);
    }

    public Task<byte[]> ConvertToPdfAsync(Stream wordStream, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var server = factory.CreateWordProcessor();
            if (!wordStream.CanSeek)
            {
                var ms = new MemoryStream();
                wordStream.CopyTo(ms);
                ms.Position = 0;
                wordStream = ms;
            }
            else
            {
                wordStream.Position = 0;
            }

            server.LoadDocument(wordStream);

            using var pdfMs = new MemoryStream();
            server.ExportToPdf(pdfMs);
            return pdfMs.ToArray();
        }, ct);
    }

    public Task<string> ExtractTextAsync(Stream wordStream, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var server = factory.CreateWordProcessor();
            if (wordStream.CanSeek) wordStream.Position = 0;
            server.LoadDocument(wordStream);
            return server.Text;
        }, ct);
    }

    public Task<byte[]> ReplaceTextAsync(Stream wordStream, IDictionary<string, string> replacements, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var server = factory.CreateWordProcessor();
            if (wordStream.CanSeek) wordStream.Position = 0;
            server.LoadDocument(wordStream);

            foreach (var kvp in replacements)
            {
                server.Document.ReplaceAll(kvp.Key, kvp.Value, SearchOptions.None);
            }

            using var ms = new MemoryStream();
            server.SaveDocument(ms, DocumentFormat.OpenXml);
            return ms.ToArray();
        }, ct);
    }
}
