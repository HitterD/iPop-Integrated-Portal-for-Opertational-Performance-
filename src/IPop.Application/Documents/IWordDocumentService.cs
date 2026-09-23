namespace IPop.Application.Documents;

public interface IWordDocumentService
{
    Task<byte[]> CreateSampleDocumentAsync(string title, string content, CancellationToken ct = default);
    Task<byte[]> ConvertToPdfAsync(Stream wordStream, CancellationToken ct = default);
    Task<string> ExtractTextAsync(Stream wordStream, CancellationToken ct = default);
    Task<byte[]> ReplaceTextAsync(Stream wordStream, IDictionary<string, string> replacements, CancellationToken ct = default);
}
