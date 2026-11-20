// Notes page — file cards: Preview opens the shared full-screen viewer with every file of the page (← → between them).
// Word (.doc/.rtf/.odt) and PowerPoint (.pptx) are shown through the PDF the server made for the card (preview-info).
using IPop.Application.Chat;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Notes
{
    [Inject] private ChatAttachments FileAtt { get; set; } = default!;

    private AttachmentDto? viewerFile;
    private IReadOnlyList<AttachmentDto>? viewerFiles;
    private Dictionary<Guid, Guid> viewerPdfs = [];

    public sealed record NoteFileRef(string Id, string? Name, long Size);

    [JSInvokable]
    public void OnPreviewFile(string id, List<NoteFileRef> files, Dictionary<string, string>? pdfs)
    {
        if (!Guid.TryParse(id, out var open)) return;
        var list = files.Select(f => Guid.TryParse(f.Id, out var g) ? new AttachmentDto(g, f.Name ?? "file", f.Size, "", null) : null)
            .OfType<AttachmentDto>().DistinctBy(a => a.Id).ToList();
        if (list.All(a => a.Id != open)) return;
        viewerPdfs = (pdfs ?? []).Where(kv => Guid.TryParse(kv.Key, out _) && Guid.TryParse(kv.Value, out _))
            .ToDictionary(kv => Guid.Parse(kv.Key), kv => Guid.Parse(kv.Value));
        // every file and converted PDF here is a notes attachment served by /api/attachments/{id} (page access)
        FileAtt.UseSharedStore(list.Select(a => a.Id).Concat(viewerPdfs.Values));
        viewerFiles = list;
        viewerFile = list.First(a => a.Id == open);
        StateHasChanged();
    }

    private Guid? ViewerPdf(AttachmentDto a) => viewerPdfs.TryGetValue(a.Id, out var pdf) ? pdf : null;

    private void CloseFileViewer() { viewerFile = null; viewerFiles = null; }
}
