using DevExpress.Drawing;
using IPop.Application.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace IPop.Infrastructure.Documents;

public static class DocumentServiceExtensions
{
    private static bool _skiaEnabled;
    private static readonly object _lock = new();

    public static IServiceCollection AddDevExpressDocumentServices(this IServiceCollection services)
    {
        lock (_lock)
        {
            if (!_skiaEnabled)
            {
                Settings.DrawingEngine = DrawingEngine.Skia;
                _skiaEnabled = true;
            }
        }

        services.AddSingleton<IDevExpressDocumentFactory, DevExpressDocumentFactory>();
        services.AddScoped<IWordDocumentService, WordDocumentService>();
        services.AddScoped<ISpreadsheetService, SpreadsheetService>();
        services.AddScoped<IPdfService, PdfService>();
        services.AddScoped<IPresentationService, PresentationService>();
        services.AddScoped<MailPdfRenderer>();
        services.AddScoped<MailHtmlRenderer>();

        return services;
    }
}
