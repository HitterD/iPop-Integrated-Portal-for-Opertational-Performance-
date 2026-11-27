using IPop.Web.Components;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Notifications
{
    private async Task LoadPreferences()
    {
        try
        {
            var pref = await Auth.GetAsync<PrefResponseDto>("/api/notifications/v1/preferences");
            if (pref is not null)
            {
                quietHoursEnabled = pref.QuietHoursEnabled;
                if (!string.IsNullOrWhiteSpace(pref.QuietStart)) quietStart = pref.QuietStart!;
                if (!string.IsNullOrWhiteSpace(pref.QuietEnd)) quietEnd = pref.QuietEnd!;
                quietDays = pref.QuietDays;
                allowImportant = pref.AllowImportant;
                mutedCategories = NotificationCategories.ParseMuted(pref.MutedTypes);
            }
        }
        catch { }
    }

    private async Task SyncLivePreferencesAsync()
    {
        suppressPrefsReload = true;
        try { await Notifs.LoadPreferencesAsync(); }
        finally { suppressPrefsReload = false; }
    }

    // ---- Select mode (Mail-style "Select" → tap rows → act on the footer) ----
    private bool AllSelected => FilteredList.Count > 0 && FilteredList.All(x => selectedIds.Contains(x.Id));

    private void SelectAllToggle()
    {
        if (AllSelected) selectedIds.Clear();
        else selectedIds = FilteredList.Select(x => x.Id).ToHashSet();
    }

    private void EndSelecting()
    {
        selecting = false;
        selectedIds.Clear();
    }

    private void RowClick(NotificationItemDto n)
    {
        if (selecting) ToggleItemSelection(n.Id);
        else SelectNotification(n);
    }

    private void SelectOnKey(KeyboardEventArgs e, NotificationItemDto item)
    {
        if (e.Key is "Enter" or " ") SelectNotification(item);
    }

    private void ToggleItemSelection(Guid id)
    {
        if (!selectedIds.Add(id)) selectedIds.Remove(id);
    }

    // ---- Snooze menu ----
    private void OpenSnoozeMenu(MouseEventArgs e)
    {
        snoozeMenuOpen = true;
        snoozeMenuAt = (e.ClientX - 150, e.ClientY + 14);
    }

    private IReadOnlyList<ContextMenu.Item> SnoozeItems
    {
        get
        {
            var list = new List<ContextMenu.Item>
            {
                ContextMenu.Item.Header("Snooze until"),
                new("In 1 hour", "clock", () => Snooze(1)),
                new($"This evening ({EveningTarget:HH:mm}{(EveningTarget.Date == DateTime.Today ? "" : ", tomorrow")})", "sunset", SnoozeTonight),
                new($"Tomorrow morning ({DateTime.Today.AddDays(1).AddHours(8):ddd} 08:00)", "sunrise", SnoozeTomorrow),
                new($"Next week ({NextMonday:ddd d MMM}, 08:00)", "calendar", SnoozeNextWeek),
            };
            if (selectedItem is not null && IsSnoozed(selectedItem))
            {
                list.Add(ContextMenu.Item.Divider());
                list.Add(new("Unsnooze", "alarm-clock-off", UnsnoozeSelected));
            }
            return list;
        }
    }

    private static DateTime EveningTarget
    {
        get
        {
            var t = DateTime.Today.AddHours(20);
            return t <= DateTime.Now ? t.AddDays(1) : t;
        }
    }

    private static DateTime NextMonday
    {
        get
        {
            var days = ((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7;
            return DateTime.Today.AddDays(days == 0 ? 7 : days).AddHours(8);
        }
    }

    private async Task TogglePrefs()
    {
        showPrefs = !showPrefs;
        if (showPrefs) await OpenMobileDetail();
        else if (mDetail) await BackToList();
    }

    // ---- Phone list/detail (see .scratch/backup-mobile/CONTRACT.md) ----
    private async Task OpenMobileDetail()
    {
        if (mDetail) return;
        try
        {
            if (!await JS.InvokeAsync<bool>("ipop.mobile.isMobile")) return;
            mRef ??= DotNetObjectReference.Create(this);
            mDetail = true;
            await JS.InvokeAsync<bool>("ipop.mobile.pushDetail", mRef, nameof(CloseMobileDetail));
        }
        catch { }
    }

    private async Task BackToList()
    {
        if (!mDetail) return;
        mDetail = false;
        if (showPrefs) showPrefs = false;
        try { await JS.InvokeVoidAsync("ipop.mobile.popDetail"); } catch { }
    }

    /// Phone/browser Back popped our history entry.
    [JSInvokable]
    public Task CloseMobileDetail()
    {
        mDetail = false; showPrefs = false;
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// Keeps the person's own quiet hours, days and "allow important"; turns them on with 22:00–07:00 only the first time.
    private async Task ToggleDnd()
    {
        if (actionBusy) return;
        actionBusy = true;
        quietHoursEnabled = !quietHoursEnabled;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Put, "/api/notifications/v1/preferences", new
            {
                quietHoursEnabled,
                quietStart,
                quietEnd,
                quietDays = quietDays ?? "",
                allowImportant,
                mutedTypes = string.Join(',', mutedCategories)
            });
            if (!res.IsSuccessStatusCode)
            {
                quietHoursEnabled = !quietHoursEnabled;
                Toasts.Show("Couldn’t change Do Not Disturb.", ToastKind.Error);
                return;
            }
            Toasts.ShowDetailed("Do Not Disturb", quietHoursEnabled ? $"On from {quietStart} to {quietEnd}. Notifications still collect here." : "Off", ToastKind.Success, "moon");
            await SyncLivePreferencesAsync();
        }
        catch
        {
            quietHoursEnabled = !quietHoursEnabled;
            Toasts.Show("Couldn’t change Do Not Disturb.", ToastKind.Error);
        }
        finally { actionBusy = false; }
    }

    private async Task ToggleMuteCategory(string type)
    {
        if (actionBusy) return;
        actionBusy = true;
        var next = !mutedCategories.Contains(type);
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, "/api/notifications/v1/preferences/mute-type", new { type, muted = next });
            if (!res.IsSuccessStatusCode)
            {
                Toasts.Show("Couldn’t update category preference.", ToastKind.Error);
                return;
            }
            if (next) mutedCategories.Add(type);
            else mutedCategories.Remove(type);
            Toasts.ShowDetailed(next ? "Muted" : "Unmuted", next ? $"{CategoryName(type)} won’t pop up or play sounds. They still collect here." : $"{CategoryName(type)} notifications are back on.", ToastKind.Success, next ? "bell-off" : "bell");
            await SyncLivePreferencesAsync();
        }
        catch
        {
            Toasts.Show("Couldn’t update category preference.", ToastKind.Error);
        }
        finally { actionBusy = false; }
    }

    private async Task OnSearchChanged()
    {
        searchCts?.Cancel();
        searchCts?.Dispose();
        var t = new CancellationTokenSource();
        searchCts = t;
        try { await Task.Delay(250, t.Token); }
        catch (OperationCanceledException) { return; }
        selectedItem = null;
        detail = null;
        selectedIds.Clear();
        await ReloadAsync();
        StateHasChanged();
    }

    private Task Snooze(double hours)
    {
        var label = hours == 1 ? "It’ll come back in an hour" : $"It’ll come back in {hours} hours";
        return SnoozeUntilTime(DateTime.UtcNow.AddHours(hours), label, rawDetail: true);
    }

    private async Task SnoozeTonight()
    {
        var target = DateTime.Today.AddHours(20);
        if (target <= DateTime.Now) target = target.AddDays(1);
        await SnoozeUntilTime(target.ToUniversalTime(), target.Date == DateTime.Today ? "this evening at 20:00" : "tomorrow at 20:00");
    }

    private async Task SnoozeTomorrow()
    {
        var target = DateTime.Today.AddDays(1).AddHours(8);
        await SnoozeUntilTime(target.ToUniversalTime(), "tomorrow at 08:00");
    }

    private async Task SnoozeNextWeek()
    {
        var days = ((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7;
        if (days == 0) days = 7;
        var target = DateTime.Today.AddDays(days).AddHours(8);
        await SnoozeUntilTime(target.ToUniversalTime(), "next Monday at 08:00");
    }

    private async Task SnoozeUntilTime(DateTime utc, string label, bool rawDetail = false)
    {
        var item = selectedItem;
        if (item is null || actionBusy) return;
        actionBusy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notifications/v1/notifications/{item.Id}/snooze", new { until = utc });
            if (!res.IsSuccessStatusCode)
            {
                Toasts.Show("Couldn’t snooze notification.", ToastKind.Error);
                return;
            }
            item.SnoozedUntil = utc;
            Toasts.ShowDetailed("Snoozed", rawDetail ? label : $"It’ll come back {label}", ToastKind.Success, "alarm-clock");
            await ReloadAsync();
        }
        catch
        {
            Toasts.Show("Couldn’t snooze notification.", ToastKind.Error);
        }
        finally { actionBusy = false; }
    }

    private async Task UnsnoozeSelected()
    {
        var item = selectedItem;
        if (item is null || actionBusy) return;
        actionBusy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notifications/v1/notifications/{item.Id}/unsnooze", new { });
            if (!res.IsSuccessStatusCode)
            {
                Toasts.Show("Couldn’t unsnooze notification.", ToastKind.Error);
                return;
            }
            item.SnoozedUntil = null;
            Toasts.ShowDetailed("Unsnoozed", "Moved back to your notifications", ToastKind.Success, "check");
            await ReloadAsync();
        }
        catch
        {
            Toasts.Show("Couldn’t unsnooze notification.", ToastKind.Error);
        }
        finally { actionBusy = false; }
    }

    private async Task<bool> DeleteItem(NotificationItemDto n)
    {
        if (actionBusy) return false;
        actionBusy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/notifications/v1/notifications/{n.Id}", null);
            if (!res.IsSuccessStatusCode)
            {
                Toasts.Show("Could not delete notification.", ToastKind.Error);
                return false;
            }
            await LeaveRowsAsync([n.Id]);
            items.RemoveAll(x => x.Id == n.Id);
            selectedIds.Remove(n.Id);
            if (selectedItem?.Id == n.Id) { selectedItem = null; detail = null; }
            Toasts.Show("Notification deleted.", ToastKind.Success);
            await ReloadAsync(keepPages: true);
            return true;
        }
        catch
        {
            Toasts.Show("Could not delete notification.", ToastKind.Error);
            return false;
        }
        finally { actionBusy = false; }
    }

    private async Task DeleteSelected()
    {
        if (selectedItem is null) return;
        if (await DeleteItem(selectedItem))
            await BackToList();
    }

    private async Task BulkDelete()
    {
        if (selectedIds.Count == 0 || actionBusy) return;
        actionBusy = true;
        try
        {
            var ids = selectedIds.ToList();
            var res = await Auth.SendAsync(HttpMethod.Post, "/api/notifications/v1/notifications/bulk-delete", new { requestIds = ids });
            if (!res.IsSuccessStatusCode)
            {
                Toasts.Show("Couldn’t delete selected notifications.", ToastKind.Error);
                return;
            }
            await LeaveRowsAsync(ids);
            items.RemoveAll(x => selectedIds.Contains(x.Id));
            if (selectedItem is not null && selectedIds.Contains(selectedItem.Id)) { selectedItem = null; detail = null; }
            EndSelecting();
            Toasts.Show("Notifications deleted.", ToastKind.Success);
            await ReloadAsync(keepPages: true);
        }
        catch
        {
            Toasts.Show("Couldn’t delete selected notifications.", ToastKind.Error);
        }
        finally { actionBusy = false; }
    }

    private async Task MuteType(string category) => await ToggleMuteCategory(category);

    /// Micro #9: rows about to disappear slide out (300ms) before the list drops them.
    private async Task LeaveRowsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0 || ids.Count > 30) return; // a mass removal just closes the gap
        try { await Task.WhenAll(ids.Select(id => JS.InvokeVoidAsync("ipopMicro.leaveSelector", $".notifs-row[data-nid=\"{id}\"]").AsTask())); }
        catch { /* motion is optional */ }
    }

    private record PrefResponseDto(bool QuietHoursEnabled, string? QuietStart, string? QuietEnd, string? MutedTypes, string? QuietDays, bool AllowImportant);
}
