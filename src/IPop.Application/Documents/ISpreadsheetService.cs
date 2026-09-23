namespace IPop.Application.Documents;

public interface ISpreadsheetService
{
    Task<byte[]> CreateSampleSpreadsheetAsync(string sheetName, IEnumerable<string[]> rows, CancellationToken ct = default);
    Task<byte[]> ConvertToPdfAsync(Stream excelStream, CancellationToken ct = default);
    Task<List<string[]>> ReadSheetDataAsync(Stream excelStream, string? sheetName = null, CancellationToken ct = default);
}
