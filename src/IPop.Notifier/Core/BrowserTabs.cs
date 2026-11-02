using System.Runtime.InteropServices;
using System.Text;

namespace IPop.Notifier.Core;

/// Brings the browser tab that already shows iPop to the front, the way a person would: restore its window,
/// select the tab, focus the window. A web page can never raise its own background tab, so without this a Notifier
/// click only changed the page in a tab the user could not see (grilled 2026-11-02, round 2).
/// Chromium browsers (Chrome, Edge, Brave) expose their tab strip to Windows UI Automation; each tab item's name is
/// the page title, which iPop ends with "— IPop".
public static class BrowserTabs
{
    /// True when an iPop browser tab was found and brought to the front.
    public static bool FocusIPopTab()
    {
        try
        {
            foreach (var hwnd in BrowserWindows())
            {
                // The active tab's title is in the window title: no tab switch needed.
                if (IPopLinks.IsIPopTitle(WindowTitle(hwnd)))
                {
                    Raise(hwnd);
                    return true;
                }
            }
            foreach (var hwnd in BrowserWindows())
            {
                if (SelectIPopTab(hwnd))
                {
                    Raise(hwnd);
                    return true;
                }
            }
        }
        catch (Exception ex) { Log.Warn("open", "focus tab: " + ex.Message); }
        return false;
    }

    // ── Windows ─────────────────────────────────────────────────────────────────

    private static List<nint> BrowserWindows()
    {
        var list = new List<nint>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = new StringBuilder(64);
            GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString() != "Chrome_WidgetWin_1") return true;
            var title = WindowTitle(h);
            // Browser windows end with the browser name; installed web apps (the iPop app itself) do not.
            if (title.EndsWith(" - Google Chrome", StringComparison.Ordinal) || title.EndsWith(" - Microsoft​ Edge", StringComparison.Ordinal)
                || title.EndsWith(" - Microsoft Edge", StringComparison.Ordinal) || title.EndsWith(" - Brave", StringComparison.Ordinal))
                list.Add(h);
            return true;
        }, 0);
        return list;
    }

    private static string WindowTitle(nint h)
    {
        var len = GetWindowTextLengthW(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static void Raise(nint h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        // Windows only lets the foreground process hand focus over; the Notifier was just clicked, so it may.
        AllowSetForegroundWindow(ASFW_ANY);
        SetForegroundWindow(h);
    }

    // ── UI Automation (native COM, no WPF) ─────────────────────────────────────

    private static bool SelectIPopTab(nint hwnd)
    {
        var uia = (IUIAutomation)new CUIAutomation();
        var window = uia.ElementFromHandle(hwnd);
        var isTab = uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_TabItemControlTypeId);
        var tabs = window.FindAll(TreeScope_Descendants, isTab);
        for (var i = 0; i < tabs.Length; i++)
        {
            var tab = tabs.GetElement(i);
            if (!IPopLinks.IsIPopTitle(tab.CurrentName)) continue;
            if (tab.GetCurrentPattern(UIA_SelectionItemPatternId) is IUIAutomationSelectionItemPattern sel)
            {
                sel.Select();
                return true;
            }
        }
        return false;
    }

    private const int TreeScope_Descendants = 4;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_TabItemControlTypeId = 50019;
    private const int UIA_SelectionItemPatternId = 10010;

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IUIAutomationElement ElementFromHandle(nint hwnd);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void get_ControlViewWalker();
        void get_ContentViewWalker();
        void get_RawViewWalker();
        void get_RawViewCondition();
        void get_ControlViewCondition();
        void get_ContentViewCondition();
        void CreateCacheRequest();
        void CreateTrueCondition();
        void CreateFalseCondition();
        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition condition);
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        void GetCurrentPropertyValue();
        void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
        void GetCachedPattern();
        void GetCachedParent();
        void GetCachedChildren();
        void get_CurrentProcessId();
        void get_CurrentControlType();
        void get_CurrentLocalizedControlType();
        string CurrentName { [return: MarshalAs(UnmanagedType.BStr)] get; }
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }
        IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationSelectionItemPattern
    {
        void Select();
    }

    // ── user32 ──────────────────────────────────────────────────────────────────

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hWnd);
    private const int SW_RESTORE = 9, ASFW_ANY = -1;
}
