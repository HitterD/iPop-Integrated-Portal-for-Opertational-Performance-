using Xunit;

namespace IPop.Tests;

/// 1.4.4: Notifier windows open at a fixed size; only Maximize changes it. A remembered drag size (sizes.json) once
/// shrank the Inbox to 269×403 with no way back, so the panels and popups must not read or resize from it.
public class NotifierFixedWindowSizeTests
{
    private static string Src(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IPop.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "IPop.Notifier", relative));
    }

    [Fact]
    public void Inbox_settings_and_devices_open_at_their_fixed_size_and_stay_maximizable()
    {
        var panels = Src(Path.Combine("Windows", "PanelWindows.cs"));
        Assert.DoesNotContain("WindowSizes.Get", panels);
        Assert.DoesNotContain("SizeKey", panels);
        Assert.Contains("new(480, 720)", panels);
        Assert.Contains("new(960, 720)", panels);
        Assert.Contains("new(1100, 720)", panels);
        Assert.Equal(3, panels.Split("FitStandardSize(").Length - 1);
        Assert.Equal(3, panels.Split("Maximizable = true").Length - 1);
    }

    [Fact]
    public void Popup_windows_are_not_edge_resizable_but_maximize()
    {
        var notice = Src(Path.Combine("Windows", "NoticeWindow.cs"));
        Assert.DoesNotContain("Resizable = true", notice);
        Assert.Contains("Maximizable = true", notice);
    }
}
