using DevExpress.Docs.Presentation;
using DevExpress.Pdf;
using DevExpress.Spreadsheet;
using DevExpress.XtraRichEdit;

namespace IPop.Infrastructure.Documents;

public interface IDevExpressDocumentFactory
{
    RichEditDocumentServer CreateWordProcessor();
    Workbook CreateWorkbook();
    PdfDocumentProcessor CreatePdfProcessor();
    Presentation CreatePresentation();
}
