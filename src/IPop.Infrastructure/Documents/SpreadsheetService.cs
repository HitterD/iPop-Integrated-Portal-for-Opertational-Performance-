using DevExpress.Spreadsheet;
using IPop.Application.Documents;

namespace IPop.Infrastructure.Documents;

public class SpreadsheetService(IDevExpressDocumentFactory factory) : ISpreadsheetService
{
    public Task<byte[]> CreateSampleSpreadsheetAsync(string sheetName, IEnumerable<string[]> rows, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var workbook = factory.CreateWorkbook();
            var sheet = workbook.Worksheets[0];
            if (!string.IsNullOrWhiteSpace(sheetName))
            {
                sheet.Name = sheetName;
            }

            int rowIndex = 0;
            foreach (var row in rows)
            {
                for (int colIndex = 0; colIndex < row.Length; colIndex++)
                {
                    sheet.Cells[rowIndex, colIndex].Value = row[colIndex];
                    if (rowIndex == 0)
                    {
                        sheet.Cells[rowIndex, colIndex].Font.Bold = true;
                    }
                }
                rowIndex++;
            }

            if (rowIndex > 0)
            {
                sheet.Columns.AutoFit(0, 10);
            }

            using var ms = new MemoryStream();
            workbook.SaveDocument(ms, DocumentFormat.Xlsx);
            return ms.ToArray();
        }, ct);
    }

    public Task<byte[]> ConvertToPdfAsync(Stream excelStream, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var workbook = factory.CreateWorkbook();
            if (excelStream.CanSeek) excelStream.Position = 0;
            workbook.LoadDocument(excelStream, DocumentFormat.Xlsx);

            using var pdfMs = new MemoryStream();
            workbook.ExportToPdf(pdfMs);
            return pdfMs.ToArray();
        }, ct);
    }

    public Task<List<string[]>> ReadSheetDataAsync(Stream excelStream, string? sheetName = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var workbook = factory.CreateWorkbook();
            if (excelStream.CanSeek) excelStream.Position = 0;
            workbook.LoadDocument(excelStream);

            var sheet = !string.IsNullOrWhiteSpace(sheetName) && workbook.Worksheets.Contains(sheetName)
                ? workbook.Worksheets[sheetName]
                : workbook.Worksheets[0];

            var range = sheet.GetDataRange();
            var result = new List<string[]>();

            for (int r = 0; r < range.RowCount; r++)
            {
                var rowData = new string[range.ColumnCount];
                for (int c = 0; c < range.ColumnCount; c++)
                {
                    rowData[c] = range[r, c].DisplayText ?? string.Empty;
                }
                result.Add(rowData);
            }

            return result;
        }, ct);
    }
}
