using System.Runtime.InteropServices;
using System.Text;

namespace IPop.Notifier.Core;

/// Brings the browser tab that already shows iPop to the front, the way a person would: restore its window,
/// select the tab, focus the window. A web page can never raise its own background tab, so without this a Notifier
/// click only changed the page in a tab the user could not see (grilled 2026-11-02, round 2).
/// Chromium browsers (Chrome, Edge, Brave) expose their tab strip to Windows UI Automation; each tab item's name is
/// the page title (IPopLinks.IsIPopTitle). Only the browser's own UI is walked — never into a page (Document), which
/// would be slow and would switch on Chrome's full accessibility mode.
public static class BrowserTabs
{
    private const int MaxDepth = 12, MaxNodes = 600;

    /// The tab that just took a page from /open.html marks its title "● New in iPop" (session-lock.js); select that
    /// tab and bring its window to the front. Only the marked tab: a locked tab or a sign-in page never takes a page.
    public static bool FocusMarkedTab()
    {
        try
        {
            var windows = BrowserWindows();
            foreach (var hwnd in windows)
                if (IPopLinks.IsMarkedTitle(WindowTitle(hwnd))) { Raise(hwnd); return true; }
            foreach (var hwnd in windows)
                if (SelectTab(hwnd, IPopLinks.IsMarkedTitle)) { Raise(hwnd); return true; }
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
            if (title.EndsWith(" - Google Chrome", StringComparison.Ordinal) || title.EndsWith(" Edge", StringComparison.Ordinal)
                || title.EndsWith(" - Brave", StringComparison.Ordinal))
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
        if (GetForegroundWindow() == h) return;
        if (SetForegroundWindow(h)) return;
        // Windows refuses focus changes from a background process; a synthetic Alt tap is the documented way the
        // shell itself lets the next SetForegroundWindow through.
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        SetForegroundWindow(h);
    }

    // ── UI Automation (native COM, no WPF) ─────────────────────────────────────

    private static bool SelectTab(nint hwnd, Func<string?, bool> match)
    {
        foreach (var tab in Tabs(hwnd, match))
            if (tab.GetCurrentPattern(UIA_SelectionItemPatternId) is IUIAutomationSelectionItemPattern sel)
            {
                sel.Select();
                return true;
            }
        return false;
    }

    /// Breadth-first over the window's own UI, children only, never into a page; stops at MaxDepth/MaxNodes.
    private static List<IUIAutomationElement> Tabs(nint hwnd, Func<string?, bool> match)
    {
        var found = new List<IUIAutomationElement>();
        var uia = (IUIAutomation)new CUIAutomation();
        var all = uia.CreateTrueCondition();
        var level = new List<IUIAutomationElement> { uia.ElementFromHandle(hwnd) };
        var visited = 0;
        for (var depth = 0; depth < MaxDepth && level.Count > 0 && visited < MaxNodes; depth++)
        {
            var next = new List<IUIAutomationElement>();
            foreach (var el in level)
            {
                var kids = el.FindAll(TreeScope_Children, all);
                for (var i = 0; i < kids.Length && visited < MaxNodes; i++)
                {
                    var k = kids.GetElement(i);
                    visited++;
                    var type = k.CurrentControlType;
                    if (type == UIA_DocumentControlTypeId) continue; // web page content: never walked
                    if (type == UIA_TabItemControlTypeId) { if (match(k.CurrentName)) found.Add(k); continue; }
                    next.Add(k);
                }
            }
            level = next;
        }
        return found;
    }

    private const int TreeScope_Children = 2;
    private const int UIA_TabItemControlTypeId = 50019;
    private const int UIA_DocumentControlTypeId = 50030;
    private const int UIA_SelectionItemPatternId = 10010;

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    // Slot order follows UIAutomationClient.h exactly; unused members are placeholders that keep the vtable aligned.
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
        IUIAutomationCondition CreateTrueCondition();
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
        int CurrentControlType { get; }
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
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x2;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hWnd);
    private const int SW_RESTORE = 9;
}
