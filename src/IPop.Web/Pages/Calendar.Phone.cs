using Microsoft.JSInterop;
namespace IPop.Web.Pages;

public partial class Calendar
{
    private bool phoneOptions, phoneGrid, phoneChromeHidden;
    private async Task SyncPhoneCalendar()
    {
        var hide = Phone.IsPhone && drawerOpen;
        if (hide == phoneChromeHidden) return;
        phoneChromeHidden = hide;
        await JS.InvokeVoidAsync("ipop.mobile.hideTabbar", hide);
    }
    private void PhoneChanged() => _ = InvokeAsync(StateHasChanged);
    private async Task PhonePickDay(DateTime day) { anchor = day.Date; miniMonth = new(anchor.Year, anchor.Month, 1); await EnsureLoadedAsync(); }
    private async Task PhoneShiftWeek(int direction) { anchor = anchor.AddDays(direction * 7); miniMonth = new(anchor.Year, anchor.Month, 1); await EnsureLoadedAsync(); }
    private async Task PhoneView(string mode) { phoneOptions = false; phoneGrid = mode is "day" or "month"; await SetViewAsync(mode); }
    private void PhoneDrawerChanged(bool open) { if (!open) CloseDrawer(); }
}
