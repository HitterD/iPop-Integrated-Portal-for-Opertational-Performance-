namespace IPop.Application.Documents;

public interface IPdfService
{
    Task<byte[]> CreateSamplePdfAsync(string title, string message, CancellationToken ct = default);
    Task<byte[]> MergePdfAsync(IEnumerable<Stream> pdfStreams, CancellationToken ct = default);
    Task<int> GetPageCountAsync(Stream pdfStream, CancellationToken ct = default);
    Task<string> ExtractTextAsync(Stream pdfStream, CancellationToken ct = default);
}
