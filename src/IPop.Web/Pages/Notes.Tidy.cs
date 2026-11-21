using IPop.Application.Notes;
using IPop.Web.Services;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

/// "Tidy up page": rule-based suggestions computed by the editor bundle (tidy.js). Applying one swaps the document in as a
/// single editor step, so Undo (the toast button or Ctrl Z) puts everything back.
public partial class Notes
{
    public sealed record TidySuggestion(string Id, string Rule, string Label, string Detail);

    private List<TidySuggestion> tidySuggestions = [];
    private int tidyCount;
    private Guid? tidyCheckedFor;
    private CancellationTokenSource? tidyCts;

    /// A quiet background check once a page is open, so the button can show how many suggestions there are.
    private Task TidyAfterRenderAsync()
    {
        if (editor is null || note is null || !CanEdit || view != View.Page || note.Kind == "board" || tidyCheckedFor == note.Id)
            return Task.CompletedTask;
        var id = note.Id;
        tidyCheckedFor = id;
        CancelAndDispose(ref tidyCts);
        var cts = tidyCts = new CancellationTokenSource();
        _ = RunDelayedTidyAsync(id, cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunDelayedTidyAsync(Guid id, CancellationToken token)
    {
        try { await Task.Delay(1200, token); }   // let the first sync land before looking at the content
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested || disposed || editor is null || note?.Id != id || !CanEdit || view != View.Page || note.Kind == "board") return;
        await InvokeAsync(async () =>
        {
            if (token.IsCancellationRequested || disposed || editor is null || note?.Id != id || !CanEdit) return;
            await RefreshTidyAsync();
            if (!disposed) StateHasChanged();
        });
    }

    private async Task RefreshTidyAsync()
    {
        if (editor is null || note is null || note.Kind == "board") { tidySuggestions = []; tidyCount = 0; return; }
        try
        {
            tidySuggestions = await editor.InvokeAsync<List<TidySuggestion>>("tidySuggestions", titleDraft) ?? [];
        }
        catch (JSException) { tidySuggestions = []; }
        catch (JSDisconnectedException) { tidySuggestions = []; }
        tidyCount = tidySuggestions.Count;
    }

    private async Task OpenTidyAsync()
    {
        await RefreshTidyAsync();
        pop = Pop.Tidy;
    }

    private async Task ApplyTidyAsync(List<string> rules)
    {
        if (editor is null || note is null || !CanEdit || note.Kind == "board" || rules.Count == 0) return;
        var id = note.Id;
        var result = await editor.InvokeAsync<TidyResult>("tidyApply", rules, titleDraft);
        if (disposed || note?.Id != id) return;
        if (!string.IsNullOrWhiteSpace(result?.Title) && string.IsNullOrWhiteSpace(titleDraft))
        {
            titleDraft = result.Title!;
            await PatchAsync(new UpdateNoteMetaRequest(titleDraft, null, null, null, null, null, null));
            if (disposed || note?.Id != id) return;
        }
        await RefreshTidyAsync();
        pop = tidySuggestions.Count == 0 ? Pop.None : Pop.Tidy;
        Toasts.Show(rules.Count == 1 ? "Applied. Press Ctrl Z to undo." : $"Applied {rules.Count} suggestions. Press Ctrl Z to undo.", ToastKind.Success);
    }

    public sealed record TidyResult(int Applied, string? Title);
}
