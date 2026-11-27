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
        Notifs.UnreadChanged += OnLiveUnreadChanged;
    }

    private void OnLiveUnreadChanged()
    {
        if (suppressLiveReload) return;
        _ = InvokeAsync(async () =>
        {
            await LiveRefreshAsync();
            StateHasChanged();
        });
    }

    private void SetShellUnread(int count)
    {
        suppressLiveReload = true;
        try { Notifs.SetUnread(count); }
        finally { suppressLiveReload = false; }
    }

    private string InboxUrl(int page, int size)
    {
        var url = $"/api/notifications/v1/inbox?page={page}&pageSize={size}";
        if (categoryFilter == "snoozed") url += "&status=snoozed";
        else if (categoryFilter == "unread") url += "&status=unread";
        else if (categoryFilter != "all") url += $"&category={categoryFilter}";
        if (!string.IsNullOrWhiteSpace(searchQuery)) url += "&q=" + Uri.EscapeDataString(searchQuery.Trim());
        return url;
    }

    private Task ReloadAsync() => ReloadAsync(keepPages: false);

    /// <summary>
    /// Full / explicit inbox reload (initial load, refresh button, category/search change, delete/snooze).
    /// keepPages keeps as many rows as are on screen (up to the server's 100).
    /// </summary>
    private async Task ReloadAsync(bool keepPages)
    {
        var seq = ++reloadSeq;
        isLoading = true;
        try
        {
            var size = keepPages ? Math.Clamp(items.Count, PageSize, 100) : PageSize;
            var res = await Auth.GetAsync<InboxResultDto>(InboxUrl(1, size));
            if (res is not null && seq == reloadSeq)
            {
                var fresh = res.Items.Select(ToItem).ToList();
                ApplyCounts(res, fresh);
                items = fresh;
                loadedPage = Math.Max(1, size / PageSize);
                hasMore = res.TotalCount > items.Count;
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
            var size = Math.Clamp(items.Count, PageSize, 100);
            var res = await Auth.GetAsync<InboxResultDto>(InboxUrl(1, size));
            if (res is null || seq != reloadSeq || categoryFilter != filterAtStart || searchQuery != searchAtStart)
                return;

            var fresh = res.Items.Select(ToItem).ToList();
            ApplyCounts(res, fresh);
            items = MergeLiveItems(items, fresh, categoryFilter, selectedItem?.Id);
            loadedPage = Math.Max(1, size / PageSize);
            hasMore = res.TotalCount > fresh.Count;

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
        Guid? selectedId)
    {
        var freshById = new Dictionary<Guid, NotificationItemDto>(fresh.Count);
        foreach (var f in fresh) freshById.TryAdd(f.Id, f);

        var merged = new List<NotificationItemDto>(current.Count + fresh.Count);
        var seen = new HashSet<Guid>(current.Count + fresh.Count);

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
            var res = await Auth.GetAsync<InboxResultDto>(InboxUrl(loadedPage + 1, PageSize));
            if (res is not null)
            {
                var fresh = res.Items.Select(ToItem).ToList();
                var seen = items.Select(i => i.Id).ToHashSet();
                ApplyCounts(res, fresh);
                items.AddRange(fresh.Where(i => seen.Add(i.Id)));
                loadedPage++;
                hasMore = res.TotalCount > items.Count && res.Items.Count > 0;
            }
        }
        catch { }
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

    private static NotificationItemDto ToItem(InboxItemResultDto i) => new(
        i.Id, i.Type, i.Title, i.Body, i.Source,
        i.At.Kind == DateTimeKind.Utc ? i.At.ToLocalTime() : i.At,
        i.IsRead, i.Priority, i.TargetUrl, i.SnoozedUntil)
    {
        EventType = i.EventType ?? i.Type,
        Category = i.Category ?? "system",
        ActorName = i.ActorName,
    };

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
            {
                item.IsRead = false;
                unreadCount++;
                unreadByCategory[item.Category] = unreadByCategory.GetValueOrDefault(item.Category) + 1;
                SetShellUnread(unreadCount);
                Layout?.SetSubtitle(unreadCount == 0 ? "All caught up" : $"{unreadCount} unread");
            }
        }
        catch { }
        finally
        {
            pendingReadIds.Remove(id);
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task BulkMarkRead()
    {
        if (selectedIds.Count == 0) return;
        var ids = selectedIds.ToList();
        var res = await Auth.SendAsync(HttpMethod.Post, "/api/notifications/v1/notifications/bulk-read", new { requestIds = ids });
        if (res.IsSuccessStatusCode)
        {
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
    }

    private async Task ToggleReadStatus(NotificationItemDto n)
    {
        if (n.IsRead)
        {
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notifications/v1/notifications/{n.Id}/unread", new { });
            if (res.IsSuccessStatusCode)
            {
                n.IsRead = false;
                unreadCount++;
                unreadByCategory[n.Category] = unreadByCategory.GetValueOrDefault(n.Category) + 1;
            }
        }
        else
        {
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notifications/v1/notifications/{n.Id}/read", new { });
            if (res.IsSuccessStatusCode)
            {
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
        }
        SetShellUnread(unreadCount);
        Layout?.SetSubtitle(unreadCount == 0 ? "All caught up" : $"{unreadCount} unread");
        StateHasChanged();
    }

    private async Task ToggleReadSelected()
    {
        if (selectedItem is null) return;
        await ToggleReadStatus(selectedItem);
    }

    private async Task MarkAllRead()
    {
        var res = await Auth.SendAsync(HttpMethod.Post, "/api/notifications/v1/inbox/read-all", new { });
        if (!res.IsSuccessStatusCode) return;
        if (categoryFilter == "unread")
        {
            await LeaveRowsAsync(items.Select(i => i.Id).ToList());
            items.Clear();
            selectedItem = null;
            detail = null;
        }
        else
        {
            foreach (var i in items) i.IsRead = true;
        }
        unreadCount = 0;
        unreadByCategory.Clear();
        SetShellUnread(0);
        Layout?.SetSubtitle("All caught up");
        Toasts.ShowDetailed("All Read", "All notifications marked as read", ToastKind.Success, "check-check");
    }

    internal sealed class NotificationItemDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
        public string? Source { get; set; }
        public DateTime At { get; set; }
        public bool IsRead { get; set; }
        public string Priority { get; set; }
        public string? TargetUrl { get; set; }
        public DateTime? SnoozedUntil { get; set; }
        public string EventType { get; set; } = "";
        public string Category { get; set; } = "system";
        public string? ActorName { get; set; }

        public NotificationItemDto(Guid id, string type, string title, string body, string? source, DateTime at, bool isRead, string priority, string? targetUrl, DateTime? snoozedUntil)
        {
            Id = id; Type = type; Title = title; Body = body; Source = source; At = at; IsRead = isRead; Priority = priority; TargetUrl = targetUrl; SnoozedUntil = snoozedUntil;
        }
    }

    private record InboxResultDto(List<InboxItemResultDto> Items, int TotalCount, int UnreadCount,
        Dictionary<string, int>? UnreadByCategory = null, int SnoozedCount = 0);
    private record InboxItemResultDto(Guid Id, string Type, string Title, string Body, string Source, DateTime At, bool IsRead, string Priority,
        string? TargetUrl, DateTime? SnoozedUntil = null, string? ActorName = null, string? EventType = null, string? Category = null);
}
