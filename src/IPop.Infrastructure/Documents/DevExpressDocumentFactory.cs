using DevExpress.Docs.Presentation;
using DevExpress.Pdf;
using DevExpress.Spreadsheet;
using DevExpress.XtraRichEdit;

namespace IPop.Infrastructure.Documents;

public class DevExpressDocumentFactory : IDevExpressDocumentFactory
{
    public RichEditDocumentServer CreateWordProcessor() => new();
    public Workbook CreateWorkbook() => new();
    public PdfDocumentProcessor CreatePdfProcessor() => new();
    public Presentation CreatePresentation() => new();
}
