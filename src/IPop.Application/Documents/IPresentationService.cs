namespace IPop.Application.Documents;

public interface IPresentationService
{
    Task<byte[]> CreateSamplePresentationAsync(string title, string subtitle, CancellationToken ct = default);
    Task<byte[]> ConvertToPdfAsync(Stream pptxStream, CancellationToken ct = default);
    Task<int> GetSlideCountAsync(Stream pptxStream, CancellationToken ct = default);
}
