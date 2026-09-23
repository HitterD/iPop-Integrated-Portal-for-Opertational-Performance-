using IPop.Application.Documents;

namespace IPop.Api.Endpoints;

public static class DocumentDemoEndpoints
{
    public static IEndpointRouteBuilder MapDocumentDemo(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/documents/demo");

        g.MapGet("/status", () => Results.Ok(new
        {
            provider = "DevExpress Office File API",
            version = "26.1.4",
            renderingEngine = "Skia (Cross-Platform)",
            supportedFormats = new[]
            {
                new { format = "DOC/DOCX", name = "Word Processing Document API", exportToPdf = true },
                new { format = "XLS/XLSX", name = "Spreadsheet Document API", exportToPdf = true },
                new { format = "PDF", name = "PDF Document API", exportToPdf = true },
                new { format = "PPTX", name = "PowerPoint Presentation API", exportToPdf = true }
            }
        }));

        g.MapGet("/word", async (IWordDocumentService wordService, CancellationToken ct) =>
        {
            var bytes = await wordService.CreateSampleDocumentAsync(
                "iPop Portal - Official Document",
                "This is a sample Word document (DOCX) generated directly by DevExpress Office File API on the iPop .NET 10 backend.\r\n" +
                "Generated on: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC",
                ct);

            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "iPop-Word-Demo.docx");
        });

        g.MapGet("/word/pdf", async (IWordDocumentService wordService, CancellationToken ct) =>
        {
            var wordBytes = await wordService.CreateSampleDocumentAsync(
                "iPop Portal - Word to PDF Conversion",
                "This document was originally created in DOCX format, then converted to PDF cross-platform using DevExpress and the Skia engine.",
                ct);

            using var ms = new MemoryStream(wordBytes);
            var pdfBytes = await wordService.ConvertToPdfAsync(ms, ct);

            return Results.File(pdfBytes, "application/pdf", "iPop-Word-Converted.pdf");
        });

        g.MapGet("/excel", async (ISpreadsheetService excelService, CancellationToken ct) =>
        {
            var rows = new List<string[]>
            {
                new[] { "ID", "Employee Name", "Department", "Attendance Status" },
                new[] { "EMP001", "Bagas Pratama", "Engineering", "Present" },
                new[] { "EMP002", "Siti Nurhaliza", "HRD", "Present" },
                new[] { "EMP003", "Ahmad Fauzi", "Finance", "Leave" }
            };

            var bytes = await excelService.CreateSampleSpreadsheetAsync("Attendance Report", rows, ct);
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "iPop-Excel-Demo.xlsx");
        });

        g.MapGet("/excel/pdf", async (ISpreadsheetService excelService, CancellationToken ct) =>
        {
            var rows = new List<string[]>
            {
                new[] { "No", "Module", "Platform", "Integration" },
                new[] { "1", "Word (DOCX)", ".NET 10", "DevExpress Document Processor" },
                new[] { "2", "Excel (XLSX)", ".NET 10", "DevExpress Spreadsheet API" },
                new[] { "3", "PDF", ".NET 10", "DevExpress PDF Document API" },
                new[] { "4", "PowerPoint (PPTX)", ".NET 10", "DevExpress Presentation API" }
            };

            var excelBytes = await excelService.CreateSampleSpreadsheetAsync("Module List", rows, ct);
            using var ms = new MemoryStream(excelBytes);
            var pdfBytes = await excelService.ConvertToPdfAsync(ms, ct);

            return Results.File(pdfBytes, "application/pdf", "iPop-Excel-Converted.pdf");
        });

        g.MapGet("/pdf", async (IPdfService pdfService, CancellationToken ct) =>
        {
            var bytes = await pdfService.CreateSamplePdfAsync(
                "iPop - PDF Document API",
                "This PDF document was generated directly by DevExpress PDF Document API on iPop .NET 10 with the Skia rendering engine.",
                ct);

            return Results.File(bytes, "application/pdf", "iPop-Pdf-Demo.pdf");
        });

        g.MapGet("/powerpoint", async (IPresentationService pptService, CancellationToken ct) =>
        {
            var bytes = await pptService.CreateSamplePresentationAsync(
                "iPop System Overview",
                "Generated with DevExpress PowerPoint Presentation API on .NET 10",
                ct);

            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.presentationml.presentation", "iPop-PowerPoint-Demo.pptx");
        });

        g.MapGet("/powerpoint/pdf", async (IPresentationService pptService, CancellationToken ct) =>
        {
            var pptBytes = await pptService.CreateSamplePresentationAsync(
                "iPop Presentation to PDF",
                "PowerPoint slide exported to PDF via DevExpress Office File API",
                ct);

            using var ms = new MemoryStream(pptBytes);
            var pdfBytes = await pptService.ConvertToPdfAsync(ms, ct);

            return Results.File(pdfBytes, "application/pdf", "iPop-PowerPoint-Converted.pdf");
        });

        return app;
    }
}
