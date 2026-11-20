namespace IPop.Application.Documents;

public interface IPdfService
{
    Task<byte[]> CreateSamplePdfAsync(string title, string message, CancellationToken ct = default);
    Task<byte[]> MergePdfAsync(IEnumerable<Stream> pdfStreams, CancellationToken ct = default);
    Task<int> GetPageCountAsync(Stream pdfStream, CancellationToken ct = default);
    Task<string> ExtractTextAsync(Stream pdfStream, CancellationToken ct = default);
    /// Page count and a JPEG of page 1 whose longest edge is <paramref name="longestEdge"/> px (null when the page could not be drawn).
    Task<PdfFirstPage> RenderFirstPageAsync(Stream pdfStream, int longestEdge, CancellationToken ct = default);
}

public record PdfFirstPage(int Pages, byte[]? Jpeg);
