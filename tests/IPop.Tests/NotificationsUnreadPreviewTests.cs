using System.Net;
using System.Net.Http.Json;
using System.Web;
using Bunit;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class NotificationsUnreadPreviewTests
{
    private sealed class ServerRow
    {
        public Guid Id { get; init; }
        public string Type { get; init; } = "mail";
        public string EventType { get; init; } = "mail.new";
        public string Category { get; init; } = "mail";
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
        public string Source { get; init; } = "mail";
        public DateTime At { get; init; }
        public bool IsRead { get; set; }
        public string Priority { get; init; } = "normal";
        public string? TargetUrl { get; init; }
        public string? ActorName { get; init; }
    }

    private sealed class FakeInboxApi : HttpMessageHandler
    {
        public List<ServerRow> Rows { get; } = [];
        public TaskCompletionSource<bool>? ReadGate { get; set; }
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Calls.Add($"{req.Method.Method} {req.RequestUri.PathAndQuery}");

            if (path == "/api/notifications/v1/preferences")
            {
                return JsonOk(new
                {
                    quietHoursEnabled = false,
                    quietStart = "22:00",
                    quietEnd = "07:00",
                    mutedTypes = "",
                    quietDays = "",
                    allowImportant = true
                });
            }

            if (path == "/api/notifications/v1/inbox")
            {
                var q = HttpUtility.ParseQueryString(req.RequestUri.Query);
                var status = q["status"];
                var category = q["category"];

                IEnumerable<ServerRow> filtered = Rows.OrderByDescending(r => r.At);
                if (status == "unread") filtered = filtered.Where(r => !r.IsRead);
                else if (status == "read") filtered = filtered.Where(r => r.IsRead);
                else if (!string.IsNullOrEmpty(category)) filtered = filtered.Where(r => r.Category == category);

                var list = filtered.ToList();
                var unreadTotal = Rows.Count(r => !r.IsRead);
                var byCat = Rows.Where(r => !r.IsRead)
                    .GroupBy(r => r.Category)
                    .ToDictionary(g => g.Key, g => g.Count());

                return JsonOk(new
                {
                    items = list.Select(r => new
                    {
                        id = r.Id,
                        type = r.Type,
                        title = r.Title,
                        body = r.Body,
                        source = r.Source,
                        at = r.At,
                        isRead = r.IsRead,
                        priority = r.Priority,
                        targetUrl = r.TargetUrl,
                        snoozedUntil = (DateTime?)null,
                        actorName = r.ActorName,
                        eventType = r.EventType,
                        category = r.Category
                    }),
                    totalCount = list.Count,
                    unreadCount = unreadTotal,
                    unreadByCategory = byCat,
                    snoozedCount = 0
                });
            }

            if (req.Method == HttpMethod.Post && path.StartsWith("/api/notifications/v1/notifications/") && path.EndsWith("/read"))
            {
                if (ReadGate is { } gate) await gate.Task;
                var idStr = path.Split('/')[5];
                if (Guid.TryParse(idStr, out var id))
                {
                    var row = Rows.FirstOrDefault(r => r.Id == id);
                    if (row is not null) row.IsRead = true;
                }
                return JsonOk(new { status = "Read" });
            }

            if (req.Method == HttpMethod.Post && path.StartsWith("/api/notifications/v1/notifications/") && path.EndsWith("/unread"))
            {
                var idStr = path.Split('/')[5];
                if (Guid.TryParse(idStr, out var id))
                {
                    var row = Rows.FirstOrDefault(r => r.Id == id);
                    if (row is not null) row.IsRead = false;
                }
                return JsonOk(new { status = "unread" });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonOk(object payload) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }

    private static (BunitContext Ctx, FakeInboxApi Api, NotificationState Notifs, BadgeState Badges) CreateContext(params ServerRow[] rows)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new FakeInboxApi();
        api.Rows.AddRange(rows);

        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        var toasts = new ToastService();
        var notifs = new NotificationState(auth, http, toasts, ctx.JSInterop.JSRuntime);
        var badges = new BadgeState(auth);
        // Mirror MainLayout.OnNotifUnreadChanged so sidebar badge is verified end-to-end.
        notifs.UnreadChanged += () => badges.Set("notif", notifs.UnreadCount);

        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(notifs);
        ctx.Services.AddSingleton(badges);
        ctx.Services.AddSingleton(new NotificationSettingsState());
        ctx.Services.AddSingleton(new PhoneState(ctx.JSInterop.JSRuntime));

        return (ctx, api, notifs, badges);
    }

    private static ServerRow MailRow(string title, string body, int minutesAgo, bool isRead = false) => new()
    {
        Id = Guid.NewGuid(),
        Type = "mail",
        EventType = "mail.new",
        Category = "mail",
        Title = title,
        Body = body,
        Source = "mail",
        At = DateTime.UtcNow.AddMinutes(-minutesAgo),
        IsRead = isRead,
        ActorName = "Sender"
    };

    private static ServerRow TaskRow(string title, string body, int minutesAgo, bool isRead = false) => new()
    {
        Id = Guid.NewGuid(),
        Type = "project",
        EventType = "task.assigned",
        Category = "task",
        Title = title,
        Body = body,
        Source = "projects",
        At = DateTime.UtcNow.AddMinutes(-minutesAgo),
        IsRead = isRead,
        ActorName = "PM"
    };

    [Fact]
    public void Clicking_item_in_Unread_marks_read_updates_all_counters_and_keeps_row_and_preview()
    {
        var r1 = MailRow("Mail One", "Body of mail one", 1);
        var r2 = TaskRow("Task Two", "Body of task two", 5);
        var (ctx, _, notifs, badges) = CreateContext(r1, r2);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".notifs-row").Count));

        // Switch to Unread filter
        cut.Find(".notifs-nav-item.k-unread").Click();
        cut.WaitForAssertion(() => Assert.Equal("Unread", cut.Find(".notifs-list-title b").TextContent));
        Assert.Equal(2, cut.FindAll(".notifs-row").Count);

        // Click first unread notification
        cut.FindAll(".notifs-row")[0].Click();

        // Item stays visible in Unread list in read style (no "unread" class)
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".notifs-row");
            Assert.Equal(2, rows.Count);
            Assert.DoesNotContain("unread", rows[0].ClassList);
            Assert.Contains("unread", rows[1].ClassList);
            Assert.Contains("sel", rows[0].ClassList);
        });

        // Preview panel stays open with the notification content
        Assert.Equal("Mail One", cut.Find(".notifs-dt-title").TextContent);
        Assert.Equal("Body of mail one", cut.Find(".notifs-dt-text").TextContent);

        // Counters drop everywhere: bell (Notifs.UnreadCount), sidebar (Badges["notif"]), Unread nav, and Mail app nav
        Assert.Equal(1, notifs.UnreadCount);
        Assert.Equal(1, badges["notif"]);
        Assert.Equal("1", cut.Find(".notifs-nav-item.k-unread .n").TextContent);
        Assert.Empty(cut.FindAll(".notifs-nav-item.k-mail .n")); // 0 unread mail -> badge hidden
        Assert.Equal("1", cut.Find(".notifs-nav-item.k-task .n").TextContent);
    }

    [Fact]
    public void Clicking_second_item_in_Unread_keeps_first_read_item_in_place_until_refresh_or_category_change()
    {
        var r1 = MailRow("First Item", "Body 1", 1);
        var r2 = TaskRow("Second Item", "Body 2", 5);
        var r3 = MailRow("Third Item", "Body 3", 10);
        var (ctx, _, notifs, _) = CreateContext(r1, r2, r3);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".notifs-row").Count));
        cut.Find(".notifs-nav-item.k-unread").Click();
        cut.WaitForAssertion(() => Assert.Equal("Unread", cut.Find(".notifs-list-title b").TextContent));

        // Click first, then second
        cut.FindAll(".notifs-row")[0].Click();
        cut.WaitForAssertion(() => Assert.Equal("First Item", cut.Find(".notifs-dt-title").TextContent));

        cut.FindAll(".notifs-row")[1].Click();
        cut.WaitForAssertion(() => Assert.Equal("Second Item", cut.Find(".notifs-dt-title").TextContent));

        // Both First Item and Second Item remain in place as read; Third Item remains unread
        var titles = cut.FindAll(".notifs-row .notifs-row-title").Select(e => e.TextContent).ToList();
        Assert.Equal(["First Item", "Second Item", "Third Item"], titles);
        var rows = cut.FindAll(".notifs-row");
        Assert.DoesNotContain("unread", rows[0].ClassList);
        Assert.DoesNotContain("unread", rows[1].ClassList);
        Assert.Contains("unread", rows[2].ClassList);
        Assert.Equal(1, notifs.UnreadCount);

        // Explicit Refresh clears the already-read items from Unread
        cut.Find("button[aria-label='Refresh']").Click();
        cut.WaitForAssertion(() =>
        {
            var refreshed = cut.FindAll(".notifs-row .notifs-row-title").Select(e => e.TextContent).ToList();
            Assert.Equal(["Third Item"], refreshed);
        });
    }

    [Fact]
    public void Realtime_update_in_Unread_adds_new_item_without_dropping_read_or_previewed_items()
    {
        var r1 = MailRow("Read Earlier", "Body 1", 5);
        var r2 = TaskRow("Currently Previewed", "Body 2", 10);
        var (ctx, api, notifs, _) = CreateContext(r1, r2);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".notifs-row").Count));
        cut.Find(".notifs-nav-item.k-unread").Click();
        cut.WaitForAssertion(() => Assert.Equal("Unread", cut.Find(".notifs-list-title b").TextContent));

        cut.FindAll(".notifs-row")[0].Click();
        cut.FindAll(".notifs-row")[1].Click();
        cut.WaitForAssertion(() => Assert.Equal("Currently Previewed", cut.Find(".notifs-dt-title").TextContent));
        Assert.Equal(0, notifs.UnreadCount);

        // Simulate SignalR push of a new unread notification
        var r3 = MailRow("Brand New Live", "Live body", 0);
        api.Rows.Add(r3);
        cut.InvokeAsync(() => notifs.SetUnread(1));

        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".notifs-row .notifs-row-title").Select(e => e.TextContent).ToList();
            Assert.Equal(["Brand New Live", "Read Earlier", "Currently Previewed"], titles);
        });

        // Preview remains open on Currently Previewed
        Assert.Equal("Currently Previewed", cut.Find(".notifs-dt-title").TextContent);
        Assert.Equal("Body 2", cut.Find(".notifs-dt-text").TextContent);
    }

    [Fact]
    public void Realtime_mark_read_from_another_tab_does_not_remove_currently_previewed_item()
    {
        var r1 = MailRow("Auto Selected", "Keep my preview", 1);
        var r2 = TaskRow("Other Unread", "Other body", 5);
        var (ctx, api, notifs, _) = CreateContext(r1, r2);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".notifs-row").Count));
        cut.Find(".notifs-nav-item.k-unread").Click();
        cut.WaitForAssertion(() => Assert.Equal("Auto Selected", cut.Find(".notifs-dt-title").TextContent));

        // Another tab marks r1 read on the server, firing UnreadChanged
        r1.IsRead = true;
        cut.InvokeAsync(() => notifs.SetUnread(1));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".notifs-row");
            Assert.Equal(2, rows.Count);
            Assert.DoesNotContain("unread", rows[0].ClassList);
            Assert.Equal("Auto Selected", cut.Find(".notifs-dt-title").TextContent);
            Assert.Equal("Keep my preview", cut.Find(".notifs-dt-text").TextContent);
        });
    }

    [Fact]
    public async Task Realtime_update_while_mark_read_request_is_in_flight_preserves_read_state_and_counts()
    {
        var r1 = MailRow("In Flight Item", "Body 1", 2);
        var r2 = TaskRow("Unread Two", "Body 2", 5);
        var (ctx, api, notifs, _) = CreateContext(r1, r2);
        using var _ = ctx;

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.ReadGate = gate;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".notifs-row").Count));
        cut.Find(".notifs-nav-item.k-unread").Click();
        cut.WaitForAssertion(() => Assert.Equal("Unread", cut.Find(".notifs-list-title b").TextContent));

        // Click r1 while POST /read is still blocked at gate
        cut.FindAll(".notifs-row")[0].Click();
        Assert.Equal(1, notifs.UnreadCount);

        // New notification arrives before POST /read completes on server
        api.Rows.Add(MailRow("Arrived Mid Flight", "Body 3", 0));
        await cut.InvokeAsync(() => notifs.SetUnread(2));

        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".notifs-row .notifs-row-title").Select(e => e.TextContent).ToList();
            Assert.Equal(["Arrived Mid Flight", "In Flight Item", "Unread Two"], titles);
        });

        // r1 stays read (not reverted to unread) and unread count accounts for pending r1 (2 unread: new + r2)
        Assert.DoesNotContain("unread", cut.FindAll(".notifs-row")[1].ClassList);
        Assert.Equal("2", cut.Find(".notifs-nav-item.k-unread .n").TextContent);

        gate.SetResult(true);
    }

    [Fact]
    public void Detail_toolbar_mark_unread_and_mark_read_in_Unread_keeps_preview_and_updates_counters()
    {
        var r1 = MailRow("Toggle Item", "Toggle body", 1);
        var r2 = TaskRow("Other Unread", "Other body", 5);
        var (ctx, _, notifs, badges) = CreateContext(r1, r2);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".notifs-row").Count));
        cut.Find(".notifs-nav-item.k-unread").Click();
        cut.WaitForAssertion(() => Assert.Equal("Unread", cut.Find(".notifs-list-title b").TextContent));

        // Click r1 -> marked read, stays in list, preview shows r1 with "Mark unread" button
        cut.FindAll(".notifs-row")[0].Click();
        cut.WaitForAssertion(() => Assert.Equal("Mark unread", cut.Find(".notifs-dt-bar .nf-tb").TextContent.Trim()));
        Assert.Equal(1, notifs.UnreadCount);
        Assert.Equal(1, badges["notif"]);

        // Click "Mark unread" in detail bar -> r1 becomes unread again, count goes back to 2
        cut.Find(".notifs-dt-bar .nf-tb").Click();
        cut.WaitForAssertion(() => Assert.Equal("Mark read", cut.Find(".notifs-dt-bar .nf-tb").TextContent.Trim()));
        Assert.Contains("unread", cut.FindAll(".notifs-row")[0].ClassList);
        Assert.Equal(2, notifs.UnreadCount);
        Assert.Equal(2, badges["notif"]);

        // Click "Mark read" in detail bar -> r1 becomes read, STAYS in list and preview STAYS open
        cut.Find(".notifs-dt-bar .nf-tb").Click();
        cut.WaitForAssertion(() => Assert.Equal("Mark unread", cut.Find(".notifs-dt-bar .nf-tb").TextContent.Trim()));
        Assert.Equal(2, cut.FindAll(".notifs-row").Count);
        Assert.DoesNotContain("unread", cut.FindAll(".notifs-row")[0].ClassList);
        Assert.Equal("Toggle Item", cut.Find(".notifs-dt-title").TextContent);
        Assert.Equal(1, notifs.UnreadCount);
    }

    [Fact]
    public void Switching_category_away_from_Unread_and_back_clears_read_items()
    {
        var r1 = MailRow("First Item", "Body 1", 1);
        var r2 = TaskRow("Second Item", "Body 2", 5);
        var (ctx, _, _, _) = CreateContext(r1, r2);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".notifs-row").Count));
        cut.Find(".notifs-nav-item.k-unread").Click();
        cut.WaitForAssertion(() => Assert.Equal("Unread", cut.Find(".notifs-list-title b").TextContent));

        cut.FindAll(".notifs-row")[0].Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("unread", cut.FindAll(".notifs-row")[0].ClassList));
        Assert.Equal(2, cut.FindAll(".notifs-row").Count);

        // Switch to Mail category, then back to Unread
        cut.Find(".notifs-nav-item.k-mail").Click();
        cut.WaitForAssertion(() => Assert.Equal("Mail", cut.Find(".notifs-list-title b").TextContent));
        cut.Find(".notifs-nav-item.k-unread").Click();
        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".notifs-row .notifs-row-title").Select(e => e.TextContent).ToList();
            Assert.Equal(["Second Item"], titles);
        });
    }
}
