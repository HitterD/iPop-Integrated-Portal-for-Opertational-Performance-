namespace IPop.Web.Services;

/// Opens the full-size photo viewer hosted once in MainLayout: a person's profile photo, or any
/// image URL (group photos) with an optional action button under it ("Change photo" for admins).
public sealed class PhotoViewerState
{
    public string? Url { get; private set; }
    public string? Name { get; private set; }
    /// Label of the button under the photo; null = no button.
    public string? ActionLabel { get; private set; }
    public Func<Task>? Action { get; private set; }
    /// Rounded-square framing for group photos (people keep the plain photo).
    public bool Square { get; private set; }
    public bool IsOpen => Url is not null;
    public event Action? Changed;

    /// No-op when the person has no photo, so avatars can call it unconditionally.
    public bool Open(string? nik, string? name)
    {
        if (IPop.Web.Models.AvatarPhotos.LargeUrl(nik) is not { } url) return false;
        Set(url, name, null, null, false);
        return true;
    }

    /// Any image URL (e.g. a group photo). No-op for an empty URL.
    public bool OpenUrl(string? url, string? name, string? actionLabel = null, Func<Task>? action = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        Set(url, name, action is null ? null : actionLabel, action, true);
        return true;
    }

    /// Runs the action button: the viewer closes first so whatever the action opens is on top.
    public async Task RunActionAsync()
    {
        var action = Action;
        Close();
        if (action is not null) await action();
    }

    public void Close()
    {
        if (Url is null) return;
        Set(null, null, null, null, false);
    }

    private void Set(string? url, string? name, string? actionLabel, Func<Task>? action, bool square)
    {
        Url = url; Name = name; ActionLabel = actionLabel; Action = action; Square = square;
        Changed?.Invoke();
    }
}
