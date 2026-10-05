// Notes page — Import (Notion zip / Markdown / HTML) and Export (Markdown / HTML / PDF).
//
// The work happens in the browser (transfer.js in the editor bundle): it reads the file, converts each page
// to the editor's schema and writes it as a Yjs state through the normal API. This part owns the dialogs:
// pick → summary → progress → result for import, and the format/scope choice for export.
using System.Text.Json;
using IPop.Application.Notes;
using IPop.Web.Components;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Notes
{
    private enum ImportStep { None, Pick, Running, Done }

    private ImportStep importStep;
    private IJSObjectReference? transferJs, importJs;
    private ElementReference importDropRef;
    private bool bindImportDrop, analyzing;
    private ImportSummary? importSummary;
    private ImportProgress? importProgress;
    private ImportResult? importResult;
    /// "root" or "inside"; insideId is the page chosen for "Inside a page".
    private string importWhere = "root";
    private Guid? importInsideId;

    private bool exportOpen, exportAll, exportBusy, exportSub = true, exportFiles = true, exportShared;
    private string exportFormat = "md";
    private Guid? exportPageId;
    private (int Done, int Total, int FilesDone, int FilesTotal) exportProgress;

    public record PreviousImport(string ImportKey, Guid Id, string Title, DateTime ImportedAt);
    public record ImportSummary(string? FileName, long Size, string? Source, int Pages, int Attachments, string? Title,
        List<PreviousImport>? Previous, string? Error);
    public record ImportRow(string Key, string Title, string? Emoji, int Depth, string Status, string Detail);
    public record ImportProgress(int Done, int Total, int FilesDone, int FilesTotal, List<ImportRow> Rows);
    public record ImportFailure(string Title, string Reason);
    public record ImportConverted(string Label, int Count, List<string> Pages);
    public record ImportResult(int Pages, int Attachments, List<ImportFailure> Failed, List<ImportConverted> Converted,
        List<string> Covers, Guid? RootId, string RootTitle, bool Stopped, string Source);

    private static readonly JsonSerializerOptions JsJson = new(JsonSerializerDefaults.Web);

    private async Task<IJSObjectReference?> TransferAsync()
    {
        if (transferJs is null && module is not null)
            transferJs = await module.InvokeAsync<IJSObjectReference>("transfer", new { apiBase = Http.BaseAddress?.ToString() ?? "", dotnet = selfRef });
        return transferJs;
    }

    // ── Import ───────────────────────────────────────────────────────────────

    private bool CanImport => sidebar is not null && Auth.Me is not null;

    /// Opens the dialog; <paramref name="parent"/> preselects "Inside a page".
    private async Task OpenImport(Guid? parent)
    {
        newMenuOpen = false; menuFor = null; pop = Pop.None;
        if (importStep == ImportStep.Running) { return; }
        if (await TransferAsync() is not { } t) return;
        if (importJs is null) importJs = await t.InvokeAsync<IJSObjectReference>("importer");
        else await importJs.InvokeVoidAsync("reset");
        importSummary = null; importProgress = null; importResult = null;
        importWhere = parent is null ? "root" : "inside";
        importInsideId = parent ?? note?.Id ?? ImportTargets().FirstOrDefault()?.Id;
        importStep = ImportStep.Pick;
        bindImportDrop = true;
    }

    /// Dropping an export onto the sidebar opens the import dialog with that file.
    private async Task ImportFirstRenderAsync()
    {
        if (await TransferAsync() is not { } t) return;
        importJs = await t.InvokeAsync<IJSObjectReference>("importer");
        try { await importJs.InvokeVoidAsync("bindDrop", sideRef, new { onlyImportable = true }); } catch (JSException) { }
    }

    private bool countUpResult;

    private async Task ImportAfterRenderAsync()
    {
        if (countUpResult && importStep == ImportStep.Done)
        {
            countUpResult = false;
            try { if (transferJs is not null) await transferJs.InvokeVoidAsync("countUp", ".nt-x-stats [data-count-up]"); } catch (JSException) { }
        }
        if (bindImportDrop && importJs is not null && importStep == ImportStep.Pick)
        {
            bindImportDrop = false;
            try { await importJs.InvokeVoidAsync("bindDrop", importDropRef); } catch (JSException) { }
        }
    }

    private async Task PickImportFile() { if (importJs is not null) await importJs.InvokeVoidAsync("pick"); }

    [JSInvokable]
    public async Task OnImportFileChosen(string name)
    {
        if (importJs is null) return;
        if (importStep == ImportStep.None) { importStep = ImportStep.Pick; importInsideId ??= note?.Id; bindImportDrop = true; }
        analyzing = true; importSummary = new ImportSummary(name, 0, null, 0, 0, null, null, null);
        StateHasChanged();
        try { importSummary = await importJs.InvokeAsync<ImportSummary>("analyze"); }
        catch (JSException) { importSummary = new ImportSummary(name, 0, null, 0, 0, null, null, "This file couldn't be read."); }
        finally { analyzing = false; }
        StateHasChanged();
    }

    private void ClearImportFile() { importSummary = null; _ = importJs?.InvokeVoidAsync("reset").AsTask(); bindImportDrop = true; }

    private Guid? ImportParent => importWhere == "inside" ? importInsideId : null;

    /// Pages the user can import into (edit access), in sidebar order.
    private List<NoteListItemDto> ImportTargets() =>
        sidebar?.Pages.Where(p => p.Access is "owner" or "edit" && !p.IsGuide).OrderBy(p => DisplayTitle(p.Title), StringComparer.CurrentCultureIgnoreCase).ToList() ?? [];

    private List<AppOption> ImportTargetOptions => ImportTargets().Select(p => new AppOption(p.Id.ToString(), $"{p.Emoji ?? "📄"} {DisplayTitle(p.Title)}")).ToList();

    private NoteListItemDto? ImportTargetPage => importInsideId is { } id ? sidebar?.Pages.FirstOrDefault(p => p.Id == id) : null;

    private async Task StartImport()
    {
        if (importJs is null || importSummary is not { Error: null, Pages: > 0 }) return;
        if (importWhere == "inside" && importInsideId is null) return;
        importStep = ImportStep.Running;
        importProgress = new ImportProgress(0, importSummary.Pages, 0, importSummary.Attachments, []);
        StateHasChanged();
        try
        {
            importResult = await importJs.InvokeAsync<ImportResult?>("run", TimeSpan.FromHours(2), ImportParent?.ToString());
        }
        catch (JSException)
        {
            importResult = new ImportResult(0, 0, [new ImportFailure(importSummary.Title ?? "Import", "The import stopped unexpectedly.")], [], [], null, importSummary.Title ?? "", false, importSummary.Source ?? "");
        }
        if (ImportParent is { } p) expanded.Add(p);
        if (importResult?.RootId is { } root) expanded.Add(root);
        await LoadSidebarAsync();
        importStep = ImportStep.Done;
        countUpResult = true;
        StateHasChanged();
        if (importResult is { RootId: { } rid, Failed.Count: 0, Converted.Count: 0, Covers.Count: <= 1, Pages: 1 })
        {
            // A clean single page: open it straight away; the toast confirms it.
            importStep = ImportStep.None;
            Toasts.Show($"“{DisplayTitle(importResult.RootTitle)}” imported", ToastKind.Success);
            Open(rid);
        }
    }

    [JSInvokable]
    public void OnImportProgress(ImportProgress p) { importProgress = p; StateHasChanged(); }

    private async Task StopImport() { if (importJs is not null) await importJs.InvokeVoidAsync("stop"); }

    private void CloseImport()
    {
        if (importStep == ImportStep.Running) return;
        importStep = ImportStep.None;
        importSummary = null;
    }

    private void OpenImported()
    {
        var id = importResult?.RootId;
        importStep = ImportStep.None;
        if (id is { } g) Open(g);
    }

    private async Task CopyImportReport()
    {
        if (importResult is not { } r || module is null) return;
        var lines = new List<string> { $"Import: {r.RootTitle}", $"{r.Pages} pages, {r.Attachments} attachments, {r.Failed.Count} problems" };
        lines.AddRange(r.Failed.Select(f => $"✕ {f.Title}: {f.Reason}"));
        lines.AddRange(r.Converted.Select(c => $"↻ {c.Count} × {c.Label}{(c.Pages.Count > 0 ? $" ({string.Join(", ", c.Pages)})" : "")}"));
        lines.AddRange(r.Covers.Select(c => $"🎨 {c}"));
        var ok = await module.InvokeAsync<bool>("copyText", string.Join("\n", lines));
        Toasts.Show(ok ? "Report copied" : "Couldn't copy the report", ok ? ToastKind.Success : ToastKind.Error);
    }

    private static string SourceLabel(string? s) => s switch { "notion" => "Notion", "markdown" => "Markdown", "html" => "HTML", _ => "a file" };

    private static string FileSize(long n) => n < 1024 ? $"{n} B" : n < 1048576 ? $"{n / 1024.0:0} KB" : $"{n / 1048576.0:0.0} MB";

    // ── Export ───────────────────────────────────────────────────────────────

    private void OpenExport(Guid? pageId, string format = "md")
    {
        newMenuOpen = false; menuFor = null; pop = Pop.None;
        if (exportBusy) return;
        exportAll = pageId is null;
        exportPageId = pageId;
        exportFormat = exportAll && format == "pdf" ? "md" : format;
        exportSub = true; exportFiles = true; exportShared = false;
        exportProgress = default;
        exportOpen = true;
    }

    private NoteListItemDto? ExportPage => exportPageId is { } id ? sidebar?.Pages.FirstOrDefault(p => p.Id == id) : null;

    private string ExportTitle => exportAll ? "all pages" : DisplayTitle(ExportPage?.Title ?? note?.Title);

    /// Pages under <paramref name="id"/> the user can see (the sidebar list is exactly that).
    private int CountDescendants(Guid id)
    {
        var n = 0;
        foreach (var k in sidebar?.Pages.Where(p => p.ParentId == id) ?? []) n += 1 + CountDescendants(k.Id);
        return n;
    }

    private List<NoteListItemDto> ExportRoots(bool shared) =>
        sidebar?.Pages.Where(p => p.ParentId is null && p.IsMine != shared && (!p.IsGuide || shared)).ToList() ?? [];

    private int ExportPageCount => exportAll
        ? ExportRoots(false).Sum(r => 1 + CountDescendants(r.Id)) + (exportShared ? ExportRoots(true).Sum(r => 1 + CountDescendants(r.Id)) : 0)
        : 1 + (exportSub && exportPageId is { } id ? CountDescendants(id) : 0);

    private async Task RunExport()
    {
        if (exportBusy || sidebar is null || await TransferAsync() is not { } t) return;
        var roots = exportAll
            ? ExportRoots(false).Select(p => p.Id).Concat(exportShared ? ExportRoots(true).Select(p => p.Id) : []).ToList()
            : exportPageId is { } id ? [id] : [];
        if (roots.Count == 0) { Toasts.Show("There are no pages to export", ToastKind.Info); return; }

        // A page opened from a link may not be in the sidebar (no visible parent chain): add it.
        var pages = sidebar.Pages.Select(p => new { id = p.Id, parentId = p.ParentId, title = DisplayTitle(p.Title), emoji = p.Emoji }).ToList();
        if (!exportAll && note is not null && pages.All(p => p.id != note.Id))
            pages.Add(new { id = note.Id, parentId = (Guid?)null, title = DisplayTitle(note.Title), emoji = note.Emoji });

        exportBusy = true;
        exportProgress = (0, ExportPageCount, 0, 0);
        StateHasChanged();
        try
        {
            await t.InvokeVoidAsync("exportPages", TimeSpan.FromHours(1), new
            {
                pages, rootIds = roots, format = exportFormat, includeSub = exportAll || exportSub,
                includeFiles = exportFiles, name = exportAll ? $"iPop Notes {DateTime.Now:yyyy-MM-dd}" : ExportTitle,
                by = Auth.Me?.Name, sharedRootIds = exportAll && exportShared ? ExportRoots(true).Select(p => p.Id) : [],
            });
            exportOpen = false;
            Toasts.Show(exportFormat == "pdf" ? "Print preview opened — choose “Save as PDF”" : "Export downloaded", ToastKind.Success);
        }
        catch (JSException) { Toasts.Show("Export failed — try again", ToastKind.Error); }
        finally { exportBusy = false; }
    }

    [JSInvokable]
    public void OnExportProgress(int done, int total, int filesDone, int filesTotal)
    {
        exportProgress = (done, total, filesDone, filesTotal);
        StateHasChanged();
    }

    private void CloseExport() { if (!exportBusy) exportOpen = false; }

    private async Task DisposeTransferAsync()
    {
        if (importJs is not null) { await importJs.InvokeVoidAsync("dispose"); await importJs.DisposeAsync(); }
        if (transferJs is not null) await transferJs.DisposeAsync();
    }
}
