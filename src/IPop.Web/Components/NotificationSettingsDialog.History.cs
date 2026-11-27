using IPop.Web.Services;

namespace IPop.Web.Components;

public partial class NotificationSettingsDialog
{
    // History tab state
    private string historyFilter = "all";
    private static readonly List<AppOption> HistoryFilterOptions =
    [
        new("all", "All"),
        new("unread", "Unread"),
        new("read", "Read"),
    ];
    private void SetHistoryFilter(string? v) => historyFilter = string.IsNullOrEmpty(v) ? "all" : v;
    private bool historyLoading;
    private bool historyHasMore = true;
    private int historyPage = 1;
    private List<HistoryItem> historyItems = [];

    private async Task LoadHistory()
    {
        historyLoading = true;
        historyPage = 1;
        historyItems = [];

        try
        {
            var statusParam = historyFilter switch
            {
                "unread" => "unread",
                "read" => "read",
                _ => null
            };

            var url = $"/api/notifications/v1/inbox?page={historyPage}&pageSize=20";
            if (statusParam is not null) url += $"&status={statusParam}";

            var response = await Auth.GetAsync<InboxResponse>(url);
            if (response is not null)
            {
                historyItems = response.Items.Select(MapToHistoryItem).ToList();
                historyHasMore = response.Items.Count >= 20;
            }
        }
        catch { }
        finally
        {
            historyLoading = false;
        }
    }

    private async Task LoadMoreHistory()
    {
        historyPage++;
        historyLoading = true;

        try
        {
            var statusParam = historyFilter switch
            {
                "unread" => "unread",
                "read" => "read",
                _ => null
            };

            var url = $"/api/notifications/v1/inbox?page={historyPage}&pageSize=20";
            if (statusParam is not null) url += $"&status={statusParam}";

            var response = await Auth.GetAsync<InboxResponse>(url);
            if (response is not null)
            {
                historyItems.AddRange(response.Items.Select(MapToHistoryItem));
                historyHasMore = response.Items.Count >= 20;
            }
        }
        catch { }
        finally
        {
            historyLoading = false;
        }
    }

    private async Task MarkHistoryRead(Guid requestId)
    {
        try
        {
            await Auth.SendAsync(HttpMethod.Post, $"/api/notifications/v1/notifications/{requestId}/read", null);
            var item = historyItems.FirstOrDefault(x => x.RequestId == requestId);
            if (item is not null) item.IsRead = true;
        }
        catch { }
    }

    private HistoryItem MapToHistoryItem(InboxItem item)
    {
        return new HistoryItem(
            item.RequestId,
            item.Type,
            item.Title,
            item.Body,
            item.Source,
            item.DeliveredAt,
            item.IsRead
        );
    }

    private string GetTypeIcon(string type) => type switch
    {
        "mention" => "at-sign",
        "chat" => "message-circle",
        "mail" => "mail",
        "tickets" => "life-buoy",
        "task" or "project" => "kanban",
        "meeting" or "call" => "calendar",
        "news" => "newspaper",
        "system" => "shield-check",
        _ => "bell"
    };

    private string GetTypeColor(string type) => type switch
    {
        "mention" => "#0d9488",
        "chat" => "#2e7d5b",
        "mail" => "#2563eb",
        "tickets" => "#0a84ff",
        "task" or "project" => "#7c3aed",
        "meeting" or "call" => "#ea580c",
        "news" => "#9333ea",
        "system" => "#6b7280",
        _ => "#6b7280"
    };

    private string FormatTime(DateTime dt)
    {
        var now = DateTime.Now;
        var diff = now - dt;

        if (diff.TotalMinutes < 1) return "Just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return dt.ToString("dd MMM yyyy");
    }

    private record InboxResponse(List<InboxItem> Items, int Total, int Unread, int Page, int PageSize);
    private record InboxItem(Guid RequestId, string Type, string Title, string Body, string Source, DateTime DeliveredAt, bool IsRead, string? Priority, string? TargetUrl);
    private class HistoryItem
    {
        public Guid RequestId { get; set; }
        public string Type { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTime DeliveredAt { get; set; }
        public bool IsRead { get; set; }

        public HistoryItem(Guid requestId, string type, string title, string body, string source, DateTime deliveredAt, bool isRead)
        {
            RequestId = requestId;
            Type = type;
            Title = title;
            Body = body;
            Source = source;
            DeliveredAt = deliveredAt;
            IsRead = isRead;
        }
    }
}
