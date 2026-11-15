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

    /// A quiet background check once a page is open, so the button can show how many suggestions there are.
    private async Task TidyAfterRenderAsync()
    {
        if (editor is null || note is null || !CanEdit || view != View.Page || tidyCheckedFor == note.Id) return;
        tidyCheckedFor = note.Id;
        await Task.Delay(1200);   // let the first sync land before looking at the content
        if (editor is null || note is null) return;
        await RefreshTidyAsync();
        StateHasChanged();
    }

    private async Task RefreshTidyAsync()
    {
        if (editor is null || note is null) { tidySuggestions = []; tidyCount = 0; return; }
        try
        {
            tidySuggestions = await editor.InvokeAsync<List<TidySuggestion>>("tidySuggestions", titleDraft) ?? [];
        }
        catch (JSException) { tidySuggestions = []; }
        tidyCount = tidySuggestions.Count;
    }

    private async Task OpenTidyAsync()
    {
        await RefreshTidyAsync();
        pop = Pop.Tidy;
    }

    private async Task ApplyTidyAsync(List<string> rules)
    {
        if (editor is null || note is null || !CanEdit || rules.Count == 0) return;
        var result = await editor.InvokeAsync<TidyResult>("tidyApply", rules, titleDraft);
        if (!string.IsNullOrWhiteSpace(result?.Title) && string.IsNullOrWhiteSpace(titleDraft))
        {
            titleDraft = result.Title!;
            await PatchAsync(new UpdateNoteMetaRequest(titleDraft, null, null, null, null, null, null));
        }
        await RefreshTidyAsync();
        pop = tidySuggestions.Count == 0 ? Pop.None : Pop.Tidy;
        Toasts.Show(rules.Count == 1 ? "Applied. Press Ctrl Z to undo." : $"Applied {rules.Count} suggestions. Press Ctrl Z to undo.", ToastKind.Success);
    }

    public sealed record TidyResult(int Applied, string? Title);
}
