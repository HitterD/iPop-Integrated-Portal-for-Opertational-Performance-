using IPop.Web.Services;

namespace IPop.Web.Pages;

public partial class Notifications
{
    private readonly HashSet<Guid> pendingReadIds = [];
    private bool suppressLiveReload;
    private int reloadSeq;

    // The server already filtered by category and search: rows read in the Unread view stay put until refresh or filter change.
    private List<NotificationItemDto> FilteredList => items.Where(n =>
        categoryFilter != "snoozed" ? !IsSnoozed(n) : IsSnoozed(n)).ToList();

    protected override async Task OnInitializedAsync()
    {
        Layout?.SetHeader("Notifications", "Notification center");
        await LoadPreferences();
        await ReloadAsync();
        Notifs.UnreadChanged += OnLiveInboxChanged;
        Notifs.InboxSynced += OnLiveInboxChanged;
        Notifs.PreferencesChanged += OnLivePreferencesChanged;
    }

    private void OnLiveInboxChanged()
    {
        if (suppressLiveReload) return;
        _ = InvokeAsync(async () =>
        {
            await LiveRefreshAsync();
            StateHasChanged();
        });
    }

    private void OnLivePreferencesChanged()
    {
        if (suppressPrefsReload) return;
        _ = InvokeAsync(async () =>
        {
            await LoadPreferences();
            StateHasChanged();
        });
    }

    private void SetShellUnread(int count)
    {
        suppressLiveReload = true;
        try { Notifs.SetUnread(count); }
        finally { suppressLiveReload = false; }
    }

    private string InboxUrl(int page, int size, DateTime? beforeAt = null, Guid? beforeId = null)
    {
        var url = $"/api/notifications/v1/inbox?page={page}&pageSize={size}";
        if (categoryFilter == "snoozed") url += "&status=snoozed";
        else if (categoryFilter == "unread") url += "&status=unread";
        else if (categoryFilter != "all") url += $"&category={categoryFilter}";
        if (!string.IsNullOrWhiteSpace(searchQuery)) url += "&q=" + Uri.EscapeDataString(searchQuery.Trim());
        if (beforeAt is { } at) url += "&beforeAt=" + Uri.EscapeDataString(at.ToUniversalTime().ToString("O"));
        if (beforeId is { } bid) url += "&beforeId=" + bid.ToString("D");
        return url;
    }

    private Task ReloadAsync() => ReloadAsync(keepPages: false);

    private async Task<(InboxResultDto First, List<NotificationItemDto> Items, bool HasMore)?> FetchUpToAsync(
        int targetCount, string filterAtStart, string searchAtStart)
    {
        const int maxServerPageSize = 100;
        var fetched = new List<NotificationItemDto>(targetCount);
        var seen = new HashSet<Guid>();
        InboxResultDto? first = null;
        InboxResultDto? lastRes = null;

        while (fetched.Count < targetCount)
        {
            var chunkSize = Math.Min(maxServerPageSize, targetCount - fetched.Count);
            var cursor = fetched.Count > 0 ? fetched[^1] : null;
            var res = await Auth.GetAsync<InboxResultDto>(
                cursor is null ? InboxUrl(1, chunkSize) : InboxUrl(1, chunkSize, cursor.AtUtc, cursor.Id));
            if (res is null || categoryFilter != filterAtStart || searchQuery != searchAtStart)
                return null;

            first ??= res;
            lastRes = res;
            var batch = res.Items.Select(ToItem).ToList();
            var added = 0;
            foreach (var item in batch)
            {
                if (seen.Add(item.Id)) { fetched.Add(item); added++; }
            }

            var chunkMore = res.HasMore ?? (res.TotalCount > fetched.Count && added > 0);
            if (!chunkMore || batch.Count < chunkSize || added == 0)
                return (first, fetched, chunkMore);
        }

        var more = lastRes!.HasMore ?? (lastRes.TotalCount > fetched.Count);
        return (first!, fetched, more);
    }

    /// <summary>
    /// Full / explicit inbox reload (initial load, refresh button, category/search change, delete/snooze).
    /// keepPages preserves all pages currently loaded (beyond the server's 100-per-request cap).
    /// </summary>
    private async Task ReloadAsync(bool keepPages)
    {
        var seq = ++reloadSeq;
        var filterAtStart = categoryFilter;
        var searchAtStart = searchQuery;
        isLoading = true;
        try
        {
            var target = keepPages ? Math.Max(PageSize, items.Count) : PageSize;
            var fetched = await FetchUpToAsync(target, filterAtStart, searchAtStart);
            if (fetched is { } f && seq == reloadSeq)
            {
                ApplyCounts(f.First, f.Items);
                items = f.Items;
                loadedPage = Math.Max(1, (int)Math.Ceiling(items.Count / (double)PageSize));
                hasMore = f.HasMore;
                SetShellUnread(unreadCount);

                if (selectedItem is null && !showPrefs && !Phone.IsPhone)
                    selectedItem = items.FirstOrDefault(i => !IsSnoozed(i) || categoryFilter == "snoozed");
                if (selectedItem is not null)
                {
                    var same = items.FirstOrDefault(x => x.Id == selectedItem.Id);
                    if (same is null) { selectedItem = null; detail = null; }
                    else
                    {
                        var changed = same.At != selectedItem.At || same.Title != selectedItem.Title;
                        selectedItem = same;
                        if (changed || detail is null) _ = LoadDetailAsync(same);
                    }
                }
            }
        }
        catch { }
        finally
        {
            if (seq == reloadSeq) isLoading = false;
        }
    }

    /// <summary>
    /// Realtime refresh (SignalR / cross-tab): merges new server rows without dropping items
    /// already marked read in the Unread view or the notification currently open in preview.
    /// </summary>
    private async Task LiveRefreshAsync()
    {
        var seq = ++reloadSeq;
        var filterAtStart = categoryFilter;
        var searchAtStart = searchQuery;
        try
        {
            var target = Math.Max(PageSize, items.Count);
            var fetched = await FetchUpToAsync(target, filterAtStart, searchAtStart);
            if (fetched is not { } f || seq != reloadSeq)
                return;

            ApplyCounts(f.First, f.Items);
            items = MergeLiveItems(items, f.Items, categoryFilter, selectedItem?.Id, f.HasMore);
            loadedPage = Math.Max(1, (int)Math.Ceiling(items.Count / (double)PageSize));
            hasMore = f.HasMore;

            if (selectedItem is not null)
            {
                var same = items.FirstOrDefault(x => x.Id == selectedItem.Id);
                if (same is not null)
                {
                    var changed = same.At != selectedItem.At || same.Title != selectedItem.Title;
                    selectedItem = same;
                    if (changed || detail is null) _ = LoadDetailAsync(same);
                }
            }
            else if (!showPrefs && !Phone.IsPhone)
            {
                selectedItem = items.FirstOrDefault(i => !IsSnoozed(i) || categoryFilter == "snoozed");
                if (selectedItem is not null && detail is null) _ = LoadDetailAsync(selectedItem);
            }
        }
        catch { }
    }

    internal static List<NotificationItemDto> MergeLiveItems(
        List<NotificationItemDto> current,
        List<NotificationItemDto> fresh,
        string categoryFilter,
        Guid? selectedId,
        bool hasMoreFromServer = false)
    {
        var freshById = new Dictionary<Guid, NotificationItemDto>(fresh.Count);
        foreach (var f in fresh) freshById.TryAdd(f.Id, f);

        var merged = new List<NotificationItemDto>(current.Count + fresh.Count);
        var seen = new HashSet<Guid>(current.Count + fresh.Count);
        var tailAt = hasMoreFromServer && fresh.Count > 0 ? fresh[^1].At : (DateTime?)null;

        foreach (var existing in current)
        {
            if (freshById.TryGetValue(existing.Id, out var updated))
            {
                merged.Add(updated);
                seen.Add(updated.Id);
            }
            else if (categoryFilter == "unread" && existing.IsRead)
            {
                merged.Add(existing);
                seen.Add(existing.Id);
            }
            else if (selectedId is { } sid && existing.Id == sid)
            {
                if (categoryFilter == "unread") existing.IsRead = true;
                merged.Add(existing);
                seen.Add(existing.Id);
            }
            else if (tailAt is { } cut && existing.At < cut)
            {
                merged.Add(existing);
                seen.Add(existing.Id);
            }
        }

        foreach (var f in fresh)
        {
            if (!seen.Add(f.Id)) continue;
            var idx = merged.FindIndex(x => x.At < f.At);
            if (idx < 0) merged.Add(f);
            else merged.Insert(idx, f);
        }

        return merged;
    }

    private async Task LoadMoreAsync()
    {
        if (loadingMore || !hasMore) return;
        loadingMore = true;
        try
        {
            var cursor = items.Count > 0 ? items[^1] : null;
            var url = cursor is null
                ? InboxUrl(loadedPage + 1, PageSize)
                : InboxUrl(1, PageSize, cursor.AtUtc, cursor.Id);
            var res = await Auth.GetAsync<InboxResultDto>(url);
            if (res is not null)
            {
                var fresh = res.Items.Select(ToItem).ToList();
                var seen = items.Select(i => i.Id).ToHashSet();
                ApplyCounts(res, fresh);
                var added = 0;
                foreach (var item in fresh)
                {
                    if (seen.Add(item.Id)) { items.Add(item); added++; }
                }
                loadedPage++;
                hasMore = res.HasMore ?? (res.TotalCount > items.Count && added > 0);
            }
        }
        catch
        {
            Toasts.Show("Couldn’t load older notifications.", ToastKind.Error);
        }
        finally { loadingMore = false; }
    }

    private void ApplyCounts(InboxResultDto res, IReadOnlyList<NotificationItemDto> serverItems)
    {
        var counts = res.UnreadByCategory is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(res.UnreadByCategory, StringComparer.OrdinalIgnoreCase);
        var totalUnread = res.UnreadCount;

        foreach (var item in serverItems)
        {
            if (!item.IsRead && pendingReadIds.Contains(item.Id))
            {
                item.IsRead = true;
                if (totalUnread > 0) totalUnread--;
                if (counts.TryGetValue(item.Category, out var c) && c > 0) counts[item.Category] = c - 1;
            }
        }

        unreadCount = totalUnread;
        snoozedCount = res.SnoozedCount;
        unreadByCategory = counts;
        Layout?.SetSubtitle(unreadCount == 0 ? "All caught up" : $"{unreadCount} unread");
    }

    private static NotificationItemDto ToItem(InboxItemResultDto i)
    {
        var utc = i.At.Kind == DateTimeKind.Local ? i.At.ToUniversalTime() : DateTime.SpecifyKind(i.At, DateTimeKind.Utc);
        return new(
            i.Id, i.Type, i.Title, i.Body, i.Source,
            utc.ToLocalTime(),
            i.IsRead, i.Priority, i.TargetUrl, i.SnoozedUntil)
        {
            AtUtc = utc,
            EventType = i.EventType ?? i.Type,
            Category = i.Category ?? "system",
            ActorName = i.ActorName,
        };
    }

    private int CountCategory(string catId) => catId switch
    {
        "all" or "unread" => unreadCount,
        "snoozed" => snoozedCount,
        _ => unreadByCategory.GetValueOrDefault(catId),
    };

    // Snoozed / unread / category / search all come from the server, so every change reloads.
    private async Task SetCategory(string cat)
    {
        if (cat == categoryFilter && !selecting) return;
        categoryFilter = cat;
        selectedItem = null;
        detail = null;
        EndSelecting();
        await ReloadAsync();
    }

    private void SelectNotification(NotificationItemDto item)
    {
        var changed = selectedItem?.Id != item.Id;
        selectedItem = item;
        showPrefs = false;
        if (changed) { replyText = ""; _ = LoadDetailAsync(item); }
        _ = InvokeAsync(async () => { await OpenMobileDetail(); StateHasChanged(); });
        if (!item.IsRead)
        {
            _ = MarkReadAsync(item.Id);
        }
    }

    private void RevertOptimisticRead(NotificationItemDto item)
    {
        item.IsRead = false;
        unreadCount++;
        unreadByCategory[item.Category] = unreadByCategory.GetValueOrDefault(item.Category) + 1;
        SetShellUnread(unreadCount);
        Layout?.SetSubtitle(unreadCount == 0 ? "All caught up" : $"{unreadCount} unread");
    }

    private async Task MarkReadAsync(Guid id)
    {
        var item = items.FirstOrDefault(x => x.Id == id);
        if (item is null || item.IsRead) return;

        item.IsRead = true;
        unreadCount = Math.Max(0, unreadCount - 1);
        if (unreadByCategory.TryGetValue(item.Category, out var c) && c > 0)
            unreadByCategory[item.Category] = c - 1;
        SetShellUnread(unreadCount);
        Layout?.SetSubtitle(unreadCount == 0 ? "All caught up" : $"{unreadCount} unread");

        pendingReadIds.Add(id);
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notifications/v1/notifications/{id}/read", new { });
            if (!res.IsSuccessStatusCode)
                RevertOptimisticRead(item);
        }
        catch
        {
            RevertOptimisticRead(item);
        }
        finally
        {
            pendingReadIds.Remove(id);
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task BulkMarkRead()
    {
        if (selectedIds.Count == 0 || actionBusy) return;
        actionBusy = true;
        try
        {
            var ids = selectedIds.ToList();
            var res = await Auth.SendAsync(HttpMethod.Post, "/api/notifications/v1/notifications/bulk-read", new { requestIds = ids });
            if (!res.IsSuccessStatusCode)
            {
                Toasts.Show("Couldn’t mark notifications as read.", ToastKind.Error);
                return;
            }
            if (categoryFilter == "unread") await LeaveRowsAsync(ids);
            foreach (var item in items.Where(x => selectedIds.Contains(x.Id))) item.IsRead = true;
            if (categoryFilter == "unread" && selectedItem is not null && selectedIds.Contains(selectedItem.Id))
            {
                selectedItem = null;
                detail = null;
            }
            EndSelecting();
            Toasts.Show("Notifications marked as read.", ToastKind.Success);
            await ReloadAsync(keepPages: true);
        }
        catch
        {
            Toasts.Show("Couldn’t mark notifications as read.", ToastKind.Error);
        }
        finally { actionBusy = false; }
    }

    private async Task ToggleReadStatus(NotificationItemDto n)
    {
        if (actionBusy) return;
        actionBusy = true;
        try
        {
            if (n.IsRead)
            {
                var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notifications/v1/notifications/{n.Id}/unread", new { });
                if (!res.IsSuccessStatusCode)
                {
                    Toasts.Show("Couldn’t update notification status.", ToastKind.Error);
                    return;
                }
                n.IsRead = false;
                unreadCount++;
                unreadByCategory[n.Category] = unreadByCategory.GetValueOrDefault(n.Category) + 1;
            }
            else
            {
                var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notifications/v1/notifications/{n.Id}/read", new { });
                if (!res.IsSuccessStatusCode)
                {
                    Toasts.Show("Couldn’t update notification status.", ToastKind.Error);
                    return;
                }
                var keepInUnread = categoryFilter == "unread" && selectedItem?.Id == n.Id;
                if (categoryFilter == "unread" && !keepInUnread)
                {
                    await LeaveRowsAsync([n.Id]);
                    items.RemoveAll(x => x.Id == n.Id);
                }
                n.IsRead = true;
                if (unreadCount > 0) unreadCount--;
                if (unreadByCategory.TryGetValue(n.Category, out var c) && c > 0) unreadByCategory[n.Category] = c - 1;
            }
            SetShellUnread(unreadCount);
            Layout?.SetSubtitle(unreadCount == 0 ? "All caught up" : $"{unreadCount} unread");
            StateHasChanged();
        }
        catch
        {
            Toasts.Show("Couldn’t update notification status.", ToastKind.Error);
        }
        finally { actionBusy = false; }
    }

    private async Task ToggleReadSelected()
    {
        if (selectedItem is null) return;
        await ToggleReadStatus(selectedItem);
    }

    private async Task MarkAllRead()
    {
        if (actionBusy) return;
        actionBusy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, "/api/notifications/v1/inbox/read-all", new { });
            if (!res.IsSuccessStatusCode)
            {
                Toasts.Show("Couldn’t mark all notifications as read.", ToastKind.Error);
                return;
            }
            if (categoryFilter == "unread")
            {
                await LeaveRowsAsync(items.Select(i => i.Id).ToList());
                items.Clear();
                selectedItem = null;
                detail = null;
            }
            else if (categoryFilter != "snoozed")
            {
                foreach (var i in items.Where(x => !IsSnoozed(x))) i.IsRead = true;
            }
            unreadCount = 0;
            unreadByCategory.Clear();
            SetShellUnread(0);
            Layout?.SetSubtitle("All caught up");
            Toasts.ShowDetailed("All Read", "All notifications marked as read", ToastKind.Success, "check-check");
        }
        catch
        {
            Toasts.Show("Couldn’t mark all notifications as read.", ToastKind.Error);
        }
        finally { actionBusy = false; }
    }

    internal sealed class NotificationItemDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
        public string? Source { get; set; }
        public DateTime At { get; set; }
        public DateTime AtUtc { get; set; }
        public bool IsRead { get; set; }
        public string Priority { get; set; }
        public string? TargetUrl { get; set; }
        public DateTime? SnoozedUntil { get; set; }
        public string EventType { get; set; } = "";
        public string Category { get; set; } = "system";
        public string? ActorName { get; set; }

        public NotificationItemDto(Guid id, string type, string title, string body, string? source, DateTime at, bool isRead, string priority, string? targetUrl, DateTime? snoozedUntil)
        {
            Id = id; Type = type; Title = title; Body = body; Source = source; At = at;
            AtUtc = at.Kind == DateTimeKind.Utc ? at : at.ToUniversalTime();
            IsRead = isRead; Priority = priority; TargetUrl = targetUrl; SnoozedUntil = snoozedUntil;
        }
    }

    private record InboxResultDto(List<InboxItemResultDto> Items, int TotalCount, int UnreadCount,
        Dictionary<string, int>? UnreadByCategory = null, int SnoozedCount = 0, bool? HasMore = null);
    private record InboxItemResultDto(Guid Id, string Type, string Title, string Body, string Source, DateTime At, bool IsRead, string Priority,
        string? TargetUrl, DateTime? SnoozedUntil = null, string? ActorName = null, string? EventType = null, string? Category = null);
}
