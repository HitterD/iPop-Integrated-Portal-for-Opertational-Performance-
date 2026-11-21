using System.Net.Http.Json;
using IPop.Application.Notes;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

/// Page tools beyond the basics: named versions (+ what changed), "Save as template" / "New from my template", and the list of
/// people who opened the page.
public partial class Notes
{
    // ── Named versions + "what changed" ──────────────────────────────────────

    private string renameVersionText = "", newVersionName = "";
    private bool namingVersion, showDiff;
    private List<DiffLine>? diffLines;
    private int diffAdded, diffRemoved;
    private bool diffBusy;

    public record DiffLine(string Kind, string Text);

    private Guid? CurrentVersionId() => versions?.FirstOrDefault()?.Id;

    private async Task NameSelectedVersionAsync()
    {
        if (note is null || !CanEdit || selVersion is not { } vid || versions is null) return;
        var pageId = note.Id;
        var name = renameVersionText.Trim();
        busy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Put, $"/api/notes/{pageId}/versions/{vid}/name", new { name });
            if (disposed || note?.Id != pageId) return;
            if (!res.IsSuccessStatusCode) { Toasts.Show(name.Length == 0 ? "Couldn't clear the name" : "Couldn't name this version", ToastKind.Error); return; }
            versions = await Auth.GetAsync<List<NoteVersionDto>>($"/api/notes/{pageId}/versions") ?? versions;
            if (disposed || note?.Id != pageId) return;
            namingVersion = false; renameVersionText = "";
            Toasts.Show(name.Length == 0 ? "Name removed" : "Version named. It is kept for good.", ToastKind.Success);
        }
        finally { busy = false; }
    }

    private async Task OnVersionNameKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await NameSelectedVersionAsync();
        else if (e.Key == "Escape") namingVersion = false;
    }

    private async Task OnSaveNamedKey(KeyboardEventArgs e) { if (e.Key == "Enter") await SaveNamedNowAsync(); }
    private async Task OnTemplateKey(KeyboardEventArgs e) { if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(templateName)) await SaveTemplateAsync(); }

    private void BeginNameVersion()
    {
        if (!CanEdit) return;
        var v = versions?.FirstOrDefault(x => x.Id == selVersion);
        renameVersionText = v?.Name ?? "";
        namingVersion = true;
    }

    /// "Save a named version now" from the history panel: snapshots the page as it is and gives it a name.
    private async Task SaveNamedNowAsync()
    {
        if (note is null || editor is null || !CanEdit) return;
        var pageId = note.Id;
        var name = newVersionName.Trim();
        if (name.Length == 0) return;
        busy = true;
        try
        {
            await TryJsAsync(() => editor.InvokeVoidAsync("flushNow").AsTask());
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{pageId}/versions", new { name });
            if (disposed || note?.Id != pageId) return;
            if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't save the version", ToastKind.Error); return; }
            versions = await Auth.GetAsync<List<NoteVersionDto>>($"/api/notes/{pageId}/versions") ?? versions;
            if (disposed || note?.Id != pageId) return;
            namingVersion = false; newVersionName = "";
            if (versions.Count > 0) await SelectVersion(versions[0].Id);
            Toasts.Show("Named version saved", ToastKind.Success);
        }
        finally { busy = false; }
    }

    /// Compares the selected version with the page as it is now (or, when the newest version is selected, with the one before it).
    private async Task ToggleDiffAsync()
    {
        if (showDiff) { showDiff = false; await Task.Yield(); await RenderPreviewAsync(); return; }
        if (editor is null || module is null || selVersionState is null || versions is null || selVersion is null) return;
        diffBusy = true;
        try
        {
            string? other;
            var idx = versions.FindIndex(x => x.Id == selVersion);
            if (idx < 0) return;
            if (idx == 0)
            {
                // Newest version: what changed since the one before it, if there is one.
                if (versions.Count < 2) { Toasts.Show("There is no earlier version to compare with", ToastKind.Info); return; }
                var prev = await Auth.GetAsync<VersionState>($"/api/notes/{note!.Id}/versions/{versions[1].Id}");
                other = prev?.State;
                if (other is null) return;
                var lines = await module.InvokeAsync<List<DiffLine>>("diffVersions", other, selVersionState);
                ApplyDiff(lines);
            }
            else
            {
                var lines = await module.InvokeAsync<List<DiffLine>>("diffVersions", selVersionState, await editor.InvokeAsync<string>("currentState"));
                ApplyDiff(lines);
            }
            await DestroyPreviewAsync();
            showDiff = true;
        }
        catch (JSException) { Toasts.Show("Couldn't compare these versions", ToastKind.Error); }
        finally { diffBusy = false; }
    }

    private void ApplyDiff(List<DiffLine>? lines)
    {
        diffLines = lines ?? [];
        diffAdded = diffLines.Count(l => l.Kind == "add");
        diffRemoved = diffLines.Count(l => l.Kind == "del");
    }

    // ── Templates ────────────────────────────────────────────────────────────

    private List<NoteTemplateDto> myTemplates = [];
    private string templateName = "";

    private async Task LoadMyTemplatesAsync()
    {
        var list = await Auth.GetAsync<List<NoteTemplateDto>>("/api/notes/templates");
        if (list is not null) { myTemplates = list; await InvokeAsync(StateHasChanged); }
    }

    private void BeginSaveTemplate()
    {
        templateName = DisplayTitle(note?.Title);
        pop = Pop.Template;
    }

    private async Task SaveTemplateAsync()
    {
        if (note is null || editor is null || !HasEditAccess) return;
        var pageId = note.Id;
        busy = true;
        try
        {
            await TryJsAsync(() => editor.InvokeVoidAsync("flushNow").AsTask());
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{pageId}/save-template", new { name = templateName.Trim() });
            if (disposed || note?.Id != pageId) return;
            if (res.IsSuccessStatusCode)
            {
                pop = Pop.None;
                Toasts.Show("Saved as a template. Find it under New page ▾.", ToastKind.Success);
                await LoadMyTemplatesAsync();
            }
            else
            {
                var code = await ErrorCodeAsync(res);
                Toasts.Show(code switch
                {
                    "empty_page" => "Write something on the page first",
                    "too_many_templates" => "You can keep up to 30 templates. Delete one first.",
                    "forbidden" => "You need edit access to save this page as a template",
                    _ => "Couldn't save the template",
                }, ToastKind.Error);
            }
        }
        finally { busy = false; }
    }

    private async Task NewFromMyTemplateAsync(Guid templateId)
    {
        if (!CanWriteNotes) return;
        newMenuOpen = false;
        busy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/templates/{templateId}/use", new { parentId = (Guid?)null });
            if (disposed) return;
            if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't create the page from this template", ToastKind.Error); return; }
            var created = await res.Content.ReadFromJsonAsync<NotePageDto>(Json);
            await LoadSidebarAsync();
            if (!disposed && created is not null) Nav.NavigateTo($"/notes/{created.Id}");
        }
        finally { busy = false; }
    }

    private async Task DeleteMyTemplateAsync(NoteTemplateDto t)
    {
        if (!await Confirm.AskAsync("Delete this template?", $"“{t.Name}” will be removed from your templates. Pages already made from it stay.", "Delete")) return;
        var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/notes/templates/{t.Id}");
        if (res.IsSuccessStatusCode) { myTemplates.RemoveAll(x => x.Id == t.Id); Toasts.Show("Template deleted", ToastKind.Success); }
        else Toasts.Show("Couldn't delete the template", ToastKind.Error);
    }

    // ── Who opened this page ─────────────────────────────────────────────────

    private List<NoteViewerDto>? viewers;

    private async Task LoadViewersAsync()
    {
        viewers = null;
        if (note is null || !HasEditAccess) return;
        var id = note.Id;
        var list = await Auth.GetAsync<List<NoteViewerDto>>($"/api/notes/{id}/viewers");
        if (note?.Id != id) return;
        viewers = list ?? [];
        await InvokeAsync(StateHasChanged);
    }
}
