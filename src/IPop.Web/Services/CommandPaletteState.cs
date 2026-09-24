namespace IPop.Web.Services;

/// Open/close state for the ⌘K palette, so ipop.js and the layout can drive it
/// without prop-drilling through every page.
public class CommandPaletteState
{
    public bool IsOpen { get; private set; }
    public event Action? Changed;

    public void Open() { if (IsOpen) return; IsOpen = true; Changed?.Invoke(); }
    public void Close() { if (!IsOpen) return; IsOpen = false; Changed?.Invoke(); }
    public void Toggle() { if (IsOpen) Close(); else Open(); }
}
