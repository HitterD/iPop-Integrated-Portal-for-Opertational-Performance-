namespace IPop.Web.Services;

/// Open/close state for the notification settings dialog, hosted once in MainLayout so any page
/// (Notifications, the status menu, the old /notifications/settings link) can open it.
public class NotificationSettingsState
{
    public bool IsOpen { get; private set; }
    public event Action? Changed;

    public void Open() { if (IsOpen) return; IsOpen = true; Changed?.Invoke(); }
    public void Close() { if (!IsOpen) return; IsOpen = false; Changed?.Invoke(); }
}
