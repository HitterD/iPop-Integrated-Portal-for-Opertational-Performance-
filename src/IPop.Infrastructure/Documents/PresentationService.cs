using DevExpress.Docs.Presentation;
using IPop.Application.Documents;

namespace IPop.Infrastructure.Documents;

public class PresentationService(IDevExpressDocumentFactory factory) : IPresentationService
{
    public Task<byte[]> CreateSamplePresentationAsync(string title, string subtitle, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var presentation = factory.CreatePresentation();

            Slide slide;
            if (presentation.Slides.Count > 0)
            {
                slide = presentation.Slides[0];
            }
            else
            {
                slide = new Slide(SlideLayoutType.Blank, "Slide 1");
                presentation.Slides.Add(slide);
            }

            var titleShape = new Shape(ShapeType.Rectangle, 100, 100, 500, 80);
            titleShape.TextArea.Text = title;
            slide.Shapes.Add(titleShape);

            var subtitleShape = new Shape(ShapeType.Rectangle, 100, 200, 500, 60);
            subtitleShape.TextArea.Text = subtitle;
            slide.Shapes.Add(subtitleShape);

            using var ms = new MemoryStream();
            presentation.SaveDocument(ms, DocumentFormat.Pptx);
            return ms.ToArray();
        }, ct);
    }

    public Task<byte[]> ConvertToPdfAsync(Stream pptxStream, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!pptxStream.CanSeek)
            {
                var ms = new MemoryStream();
                pptxStream.CopyTo(ms);
                ms.Position = 0;
                pptxStream = ms;
            }
            else
            {
                pptxStream.Position = 0;
            }

            using var presentation = new Presentation(pptxStream, DocumentFormat.Pptx);

            using var pdfMs = new MemoryStream();
            presentation.ExportToPdf(pdfMs);
            return pdfMs.ToArray();
        }, ct);
    }

    public Task<int> GetSlideCountAsync(Stream pptxStream, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (pptxStream.CanSeek) pptxStream.Position = 0;
            using var presentation = new Presentation(pptxStream, DocumentFormat.Pptx);
            return presentation.Slides?.Count ?? 0;
        }, ct);
    }
}
