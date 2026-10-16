namespace IPop.Web.Services;

/// Phone More sheet and the Edit tabs sheet opened from it (mockup v1 frames g, h). Edit tabs stacks on
/// top of More, so phone Back closes Edit tabs first and lands on More, then closes More.
public sealed class MoreFlow
{
    public bool MoreOpen { get; private set; }
    public bool EditTabsOpen { get; private set; }

    public void ToggleMore() { if (MoreOpen) CloseAll(); else MoreOpen = true; }
    public void OpenEditTabs() { MoreOpen = true; EditTabsOpen = true; }
    public void SetEditTabs(bool open) => EditTabsOpen = open && MoreOpen;
    public void SetMore(bool open) { if (open) MoreOpen = true; else CloseAll(); }
    public void CloseAll() { MoreOpen = false; EditTabsOpen = false; }

    private string? location;

    /// A real navigation closes the sheets. Phone Back on a sheet pops its own same-URL history entry,
    /// which Blazor also reports as a location change: that one must not close the sheet underneath.
    public void OnLocationChanged(string uri)
    {
        var u = uri.Split('#')[0];
        if (location is not null && u != location) CloseAll();
        location = u;
    }
}
