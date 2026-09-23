using IPop.Infrastructure.Documents;
using Xunit;

namespace IPop.Tests.Documents;

public class DevExpressDocumentTests
{
    private readonly IDevExpressDocumentFactory _factory;
    private readonly WordDocumentService _wordService;
    private readonly SpreadsheetService _spreadsheetService;
    private readonly PdfService _pdfService;
    private readonly PresentationService _presentationService;

    public DevExpressDocumentTests()
    {
        // Ensure Skia cross-platform engine is enabled
        DevExpress.Drawing.Settings.DrawingEngine = DevExpress.Drawing.DrawingEngine.Skia;

        _factory = new DevExpressDocumentFactory();
        _wordService = new WordDocumentService(_factory);
        _spreadsheetService = new SpreadsheetService(_factory);
        _pdfService = new PdfService(_factory);
        _presentationService = new PresentationService(_factory);
    }

    [Fact]
    public void DocumentFactory_Should_Create_All_Engines()
    {
        using var word = _factory.CreateWordProcessor();
        using var excel = _factory.CreateWorkbook();
        using var pdf = _factory.CreatePdfProcessor();
        using var ppt = _factory.CreatePresentation();

        Assert.NotNull(word);
        Assert.NotNull(excel);
        Assert.NotNull(pdf);
        Assert.NotNull(ppt);
    }

    [Fact]
    public async Task WordDocumentService_Should_Create_Docx_Extract_Text_And_Convert_To_Pdf()
    {
        var title = "Surat Keputusan Direksi";
        var content = "Menimbang dan memutuskan implementasi iPop Document Services.";

        var docxBytes = await _wordService.CreateSampleDocumentAsync(title, content);
        Assert.NotNull(docxBytes);
        Assert.True(docxBytes.Length > 0);

        using var docxStream = new MemoryStream(docxBytes);
        var extracted = await _wordService.ExtractTextAsync(docxStream);
        Assert.Contains(title, extracted);
        Assert.Contains(content, extracted);

        docxStream.Position = 0;
        var pdfBytes = await _wordService.ConvertToPdfAsync(docxStream);
        Assert.NotNull(pdfBytes);
        Assert.True(pdfBytes.Length > 0);

        // Verify PDF signature (%PDF)
        Assert.Equal((byte)'%', pdfBytes[0]);
        Assert.Equal((byte)'P', pdfBytes[1]);
        Assert.Equal((byte)'D', pdfBytes[2]);
        Assert.Equal((byte)'F', pdfBytes[3]);
    }

    [Fact]
    public async Task SpreadsheetService_Should_Create_Xlsx_Read_Data_And_Convert_To_Pdf()
    {
        var rows = new List<string[]>
        {
            new[] { "NIK", "Nama", "Departemen" },
            new[] { "1001", "Bagas", "IT Application" },
            new[] { "1002", "Andi", "Finance" }
        };

        var xlsxBytes = await _spreadsheetService.CreateSampleSpreadsheetAsync("Karyawan", rows);
        Assert.NotNull(xlsxBytes);
        Assert.True(xlsxBytes.Length > 0);

        using var xlsxStream = new MemoryStream(xlsxBytes);
        var readRows = await _spreadsheetService.ReadSheetDataAsync(xlsxStream, "Karyawan");
        Assert.NotNull(readRows);
        Assert.True(readRows.Count >= 3);
        Assert.Equal("NIK", readRows[0][0]);
        Assert.Equal("Bagas", readRows[1][1]);
        Assert.Equal("Finance", readRows[2][2]);

        xlsxStream.Position = 0;
        var pdfBytes = await _spreadsheetService.ConvertToPdfAsync(xlsxStream);
        Assert.NotNull(pdfBytes);
        Assert.True(pdfBytes.Length > 0);
        Assert.Equal((byte)'%', pdfBytes[0]);
        Assert.Equal((byte)'P', pdfBytes[1]);
        Assert.Equal((byte)'D', pdfBytes[2]);
        Assert.Equal((byte)'F', pdfBytes[3]);
    }

    [Fact]
    public async Task PdfService_Should_Create_Pdf_Count_Pages_And_Merge()
    {
        var title = "Dokumen Verifikasi PDF";
        var message = "Halaman pertama dokumen verifikasi.";

        var pdf1Bytes = await _pdfService.CreateSamplePdfAsync(title, message);
        Assert.NotNull(pdf1Bytes);
        Assert.True(pdf1Bytes.Length > 0);

        using var pdf1Stream = new MemoryStream(pdf1Bytes);
        var pageCount1 = await _pdfService.GetPageCountAsync(pdf1Stream);
        Assert.True(pageCount1 >= 1);

        pdf1Stream.Position = 0;
        var extractedText = await _pdfService.ExtractTextAsync(pdf1Stream);
        Assert.Contains(title, extractedText);

        var pdf2Bytes = await _pdfService.CreateSamplePdfAsync("Halaman Kedua", "Isi halaman kedua.");
        using var pdf2Stream = new MemoryStream(pdf2Bytes);

        pdf1Stream.Position = 0;
        pdf2Stream.Position = 0;
        var mergedBytes = await _pdfService.MergePdfAsync(new[] { pdf1Stream, pdf2Stream });
        Assert.NotNull(mergedBytes);
        Assert.True(mergedBytes.Length > 0);

        using var mergedStream = new MemoryStream(mergedBytes);
        var mergedPageCount = await _pdfService.GetPageCountAsync(mergedStream);
        Assert.Equal(pageCount1 + 1, mergedPageCount);
    }

    [Fact]
    public async Task PresentationService_Should_Create_Pptx_Count_Slides_And_Convert_To_Pdf()
    {
        var title = "iPop Architecture Overview";
        var subtitle = "Modular Clean Architecture with DevExpress Office File API";

        var pptxBytes = await _presentationService.CreateSamplePresentationAsync(title, subtitle);
        Assert.NotNull(pptxBytes);
        Assert.True(pptxBytes.Length > 0);

        using var pptxStream = new MemoryStream(pptxBytes);
        var slideCount = await _presentationService.GetSlideCountAsync(pptxStream);
        Assert.Equal(1, slideCount);

        pptxStream.Position = 0;
        var pdfBytes = await _presentationService.ConvertToPdfAsync(pptxStream);
        Assert.NotNull(pdfBytes);
        Assert.True(pdfBytes.Length > 0);
        Assert.Equal((byte)'%', pdfBytes[0]);
        Assert.Equal((byte)'P', pdfBytes[1]);
        Assert.Equal((byte)'D', pdfBytes[2]);
        Assert.Equal((byte)'F', pdfBytes[3]);
    }
}

public class MailPdfRendererTests
{
    private readonly MailPdfRenderer _renderer;
    private readonly PdfService _pdf;

    public MailPdfRendererTests()
    {
        DevExpress.Drawing.Settings.DrawingEngine = DevExpress.Drawing.DrawingEngine.Skia;
        var factory = new DevExpressDocumentFactory();
        _renderer = new MailPdfRenderer(factory);
        _pdf = new PdfService(factory);
    }

    private static MailPdfRenderer.MailPdfModel Sample(string subject, string body, bool html = false) =>
        new(subject, body, html, "Andi Prasetyo", "1999001", "Sari Wulandari; Budi Santoso",
            null, "normal", DateTime.UtcNow.AddHours(-2));

    [Fact]
    public async Task Render_plain_mail_produces_valid_pdf_with_content()
    {
        var bytes = await _renderer.RenderMailAsync(Sample("Laporan Mingguan", "Isi laporan minggu ke-38."));

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 500);
        Assert.Equal((byte)'%', bytes[0]);
        var text = await _pdf.ExtractTextAsync(new MemoryStream(bytes));
        Assert.Contains("Laporan Mingguan", text);
        Assert.Contains("Andi Prasetyo", text);
        Assert.Contains("Isi laporan minggu ke-38", text);
    }

    [Fact]
    public async Task Render_html_mail_keeps_formatted_text()
    {
        var bytes = await _renderer.RenderMailAsync(Sample("Surat HTML", "<p>Halo <b>tim</b>, ini <i>penting</i>.</p>", html: true));

        var text = await _pdf.ExtractTextAsync(new MemoryStream(bytes));
        Assert.Contains("Halo", text);
        Assert.Contains("tim", text);
        Assert.Contains("penting", text);
    }

    [Fact]
    public async Task Merged_mails_start_each_letter_on_new_page()
    {
        var mails = new[]
        {
            Sample("Surat Pertama", "Isi pertama."),
            Sample("Surat Kedua", "Isi kedua."),
        };
        var bytes = await _renderer.RenderMergedAsync(mails);

        var pages = await _pdf.GetPageCountAsync(new MemoryStream(bytes));
        Assert.True(pages >= 2);
        var text = await _pdf.ExtractTextAsync(new MemoryStream(bytes));
        Assert.Contains("Surat Pertama", text);
        Assert.Contains("Surat Kedua", text);
    }
}
