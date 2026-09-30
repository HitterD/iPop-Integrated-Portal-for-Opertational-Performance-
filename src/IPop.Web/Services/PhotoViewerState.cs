namespace IPop.Web.Services;

/// Opens the full-size profile photo viewer hosted once in MainLayout.
public sealed class PhotoViewerState
{
    public string? Url { get; private set; }
    public string? Name { get; private set; }
    public bool IsOpen => Url is not null;
    public event Action? Changed;

    /// No-op when the person has no photo, so avatars can call it unconditionally.
    public bool Open(string? nik, string? name)
    {
        if (IPop.Web.Models.AvatarPhotos.LargeUrl(nik) is not { } url) return false;
        Url = url;
        Name = name;
        Changed?.Invoke();
        return true;
    }

    public void Close()
    {
        if (Url is null) return;
        Url = null;
        Name = null;
        Changed?.Invoke();
    }
}
