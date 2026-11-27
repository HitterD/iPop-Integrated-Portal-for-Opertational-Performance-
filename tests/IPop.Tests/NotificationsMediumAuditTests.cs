using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Web;
using Bunit;
using IPop.Api.Endpoints;
using IPop.Application.Auth;
using IPop.Application.Notifications;
using IPop.Application.Tickets;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

public class NotificationsMediumAuditTests
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
        public DateTime? SnoozedUntil { get; set; }
        public string? ActorName { get; init; } = "Sender";
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public List<ServerRow> Rows { get; } = [];
        public bool QuietHoursEnabled { get; set; }
        public HttpStatusCode FailNextAction { get; set; } = HttpStatusCode.OK;
        public List<string> Calls { get; } = [];
        public List<(string Kind, Guid Id)> ReadByTargets { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Calls.Add($"{req.Method.Method} {req.RequestUri.PathAndQuery}");

            if (path == "/api/notifications/v1/preferences")
            {
                if (req.Method == HttpMethod.Put && FailNextAction != HttpStatusCode.OK)
                    return new HttpResponseMessage(FailNextAction);
                return JsonOk(new
                {
                    quietHoursEnabled = QuietHoursEnabled,
                    quietStart = "22:00",
                    quietEnd = "07:00",
                    mutedTypes = "",
                    quietDays = "",
                    allowImportant = true,
                    timeZoneId = "SE Asia Standard Time"
                });
            }

            if (path == "/api/notifications/v1/inbox")
            {
                var q = HttpUtility.ParseQueryString(req.RequestUri.Query);
                var status = q["status"];
                var category = q["category"];
                var search = q["q"];
                var page = int.TryParse(q["page"], out var p) ? Math.Max(1, p) : 1;
                var size = int.TryParse(q["pageSize"], out var s) ? Math.Clamp(s, 1, 100) : 20;
                var hasCursor = DateTime.TryParse(q["beforeAt"], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var beforeAt);
                Guid? beforeId = Guid.TryParse(q["beforeId"], out var bid) ? bid : null;

                var now = DateTime.UtcNow;
                IEnumerable<ServerRow> filtered = Rows;
                if (!string.IsNullOrWhiteSpace(search))
                    filtered = filtered.Where(r => r.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || r.Body.Contains(search, StringComparison.OrdinalIgnoreCase));
                if (status == "snoozed")
                    filtered = filtered.Where(r => r.SnoozedUntil is { } sn && sn > now);
                else if (status == "unread")
                    filtered = filtered.Where(r => !r.IsRead && (r.SnoozedUntil is null || r.SnoozedUntil <= now));
                else
                {
                    filtered = filtered.Where(r => r.SnoozedUntil is null || r.SnoozedUntil <= now);
                    if (!string.IsNullOrEmpty(category)) filtered = filtered.Where(r => r.Category == category);
                }

                var orderedAll = filtered.OrderByDescending(r => r.At).ThenByDescending(r => r.Id).ToList();
                var totalCount = orderedAll.Count;
                IEnumerable<ServerRow> windowed = orderedAll;
                bool hasMore;
                if (hasCursor)
                {
                    var utc = beforeAt.Kind == DateTimeKind.Utc ? beforeAt : beforeAt.ToUniversalTime();
                    windowed = orderedAll.Where(r => r.At < utc || (r.At == utc && beforeId is { } id && r.Id.CompareTo(id) < 0));
                    var remaining = windowed.ToList();
                    var take = remaining.Take(size).ToList();
                    hasMore = remaining.Count > take.Count;
                    windowed = take;
                }
                else
                {
                    var take = orderedAll.Skip((page - 1) * size).Take(size).ToList();
                    hasMore = totalCount > (page - 1) * size + take.Count;
                    windowed = take;
                }

                var list = windowed.ToList();
                var unreadTotal = Rows.Count(r => !r.IsRead && (r.SnoozedUntil is null || r.SnoozedUntil <= now));
                var byCat = Rows.Where(r => !r.IsRead && (r.SnoozedUntil is null || r.SnoozedUntil <= now))
                    .GroupBy(r => r.Category).ToDictionary(g => g.Key, g => g.Count());

                return JsonOk(new
                {
                    items = list.Select(r => new
                    {
                        id = r.Id, type = r.Type, title = r.Title, body = r.Body, source = r.Source,
                        at = r.At, isRead = r.IsRead, priority = r.Priority, targetUrl = r.TargetUrl,
                        snoozedUntil = r.SnoozedUntil, actorName = r.ActorName, eventType = r.EventType, category = r.Category
                    }),
                    totalCount,
                    unreadCount = unreadTotal,
                    unreadByCategory = byCat,
                    snoozedCount = Rows.Count(r => r.SnoozedUntil is { } sn && sn > now),
                    hasMore
                });
            }

            if (req.Method == HttpMethod.Post && path == "/api/notifications/v1/read-by-target")
            {
                var body = await req.Content!.ReadFromJsonAsync<ReadByTargetReq>(cancellationToken: ct);
                if (body is not null) ReadByTargets.Add((body.Kind, body.Id));
                return JsonOk(new { updated = 1 });
            }

            if (req.Method == HttpMethod.Get && path == "/api/tickets")
                return JsonOk(new TicketListDto([], null, false, false));

            if (req.Method == HttpMethod.Post && path == "/api/tickets/sync")
                return JsonOk(new TicketListDto([], DateTime.UtcNow, false, false));

            if (req.Method == HttpMethod.Get && path.StartsWith("/api/tickets/") && !path.EndsWith("/messages"))
            {
                var idStr = path.Split('/')[3];
                if (Guid.TryParse(idStr, out var tid))
                    return JsonOk(new TicketDetailDto(tid, "051026-INS-0001", null, "IPOP", "IN_PROGRESS", "MEDIUM",
                        "it-support", "IT Support", "GENERAL", "Printer macet", null, null, null, null, null, null, null,
                        null, null, DateTime.UtcNow, DateTime.UtcNow, null, null, null, null, [], []));
            }

            if (req.Method == HttpMethod.Get && path.EndsWith("/messages"))
                return JsonOk(new TicketMessagesDto([], 0, null, false, null));

            if (FailNextAction != HttpStatusCode.OK &&
                (path.StartsWith("/api/notifications/v1/notifications/")
                 || path == "/api/notifications/v1/inbox/read-all"
                 || path == "/api/notifications/v1/preferences/mute-type"))
            {
                return new HttpResponseMessage(FailNextAction);
            }

            if (req.Method == HttpMethod.Post && path.StartsWith("/api/notifications/v1/notifications/") && path.EndsWith("/read"))
            {
                if (Guid.TryParse(path.Split('/')[5], out var id) && Rows.FirstOrDefault(r => r.Id == id) is { } row)
                    row.IsRead = true;
                return JsonOk(new { status = "Read" });
            }

            if (req.Method == HttpMethod.Delete && path.StartsWith("/api/notifications/v1/notifications/"))
            {
                if (Guid.TryParse(path.Split('/')[5], out var id))
                    Rows.RemoveAll(r => r.Id == id);
                return JsonOk(new { success = true });
            }

            return JsonOk(new { ok = true });
        }

        private sealed record ReadByTargetReq(string Kind, Guid Id);

        private static HttpResponseMessage JsonOk(object payload) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }

    private static (BunitContext Ctx, FakeApi Api, NotificationState Notifs, ToastService Toasts, NavigationManager Nav) CreateContext(params ServerRow[] rows)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<double[]>("ipop.measure", _ => true).SetResult([180, 200]);
        ctx.JSInterop.Setup<double>("ipop.viewportWidth").SetResult(1280);
        ctx.JSInterop.Setup<double>("ipop.viewportHeight").SetResult(800);
        var api = new FakeApi();
        api.Rows.AddRange(rows);

        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        typeof(AuthState).GetField("_accessToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(auth, "test-token");
        typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth, new DirectoryEmployeeDto("U1", "Jane Smith", "", "", "", "", "", "", "", "", null, "on"));
        var toasts = new ToastService();
        var notifs = new NotificationState(auth, http, toasts, ctx.JSInterop.JSRuntime);

        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(notifs);
        ctx.Services.AddSingleton(new BadgeState(auth));
        ctx.Services.AddSingleton(new NotificationSettingsState());
        ctx.Services.AddSingleton(new PhoneState(ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton<TicketApi>();
        ctx.Services.AddSingleton<PageCache>();
        ctx.Services.AddSingleton<PeopleLinks>();
        ctx.Services.AddSingleton<CalendarLinks>();
        ctx.Services.AddSingleton<ConfirmService>();
        ctx.Services.AddSingleton<OverlayStack>();

        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        return (ctx, api, notifs, toasts, nav);
    }

    private static ServerRow Row(string title, int minutesAgo, bool isRead = false, string? targetUrl = null, string cat = "mail", DateTime? at = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = cat == "tickets" ? "system" : cat,
        EventType = cat == "tickets" ? "ticket.assigned" : "mail.new",
        Category = cat,
        Title = title,
        Body = $"Body for {title}",
        Source = cat,
        At = at ?? DateTime.UtcNow.AddMinutes(-minutesAgo),
        IsRead = isRead,
        TargetUrl = targetUrl
    };

    [Fact]
    public void Action_errors_show_error_toast_instead_of_failing_silently()
    {
        var r1 = Row("First", 1, isRead: false);
        var r2 = Row("Second", 2, isRead: false);
        var (ctx, api, _, toasts, _) = CreateContext(r1, r2);
        using var _ = ctx;
        var errors = new List<string>();
        toasts.Changed += () =>
        {
            foreach (var t in toasts.Active.Where(x => x.Kind == ToastKind.Error))
                if (!errors.Contains(t.Message)) errors.Add(t.Message);
        };

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".notifs-row").Count));

        api.FailNextAction = HttpStatusCode.InternalServerError;

        // 1. Mark all read error
        cut.Find(".notifs-toolbar-actions button:nth-child(1)").Click();
        cut.WaitForAssertion(() => Assert.Contains(errors, m => m.Contains("mark all", StringComparison.OrdinalIgnoreCase)));

        // 2. Toggle read error
        cut.Find(".notifs-dt-bar .nf-tb").Click();
        cut.WaitForAssertion(() => Assert.Contains(errors, m => m.Contains("update notification status", StringComparison.OrdinalIgnoreCase)));

        // 3. Mute type error
        cut.Find(".notifs-dt-bar button[aria-pressed]").Click();
        cut.WaitForAssertion(() => Assert.Contains(errors, m => m.Contains("category preference", StringComparison.OrdinalIgnoreCase)));

        // 4. Snooze error
        cut.Find(".notifs-dt-bar button[aria-haspopup='menu']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ctx-item")));
        cut.FindAll(".ctx-item").First(e => e.TextContent.Contains("In 1 hour")).Click();
        cut.WaitForAssertion(() => Assert.Contains(errors, m => m.Contains("snooze", StringComparison.OrdinalIgnoreCase)));

        // 5. Bulk mark read & Bulk delete errors
        cut.Find(".notifs-list-head button.nf-link").Click(); // Select
        cut.FindAll(".notifs-row")[0].Click();
        cut.Find(".notifs-bulk button:nth-child(1)").Click();
        cut.WaitForAssertion(() => Assert.Contains(errors, m => m.Contains("mark notifications as read", StringComparison.OrdinalIgnoreCase)));
        cut.Find(".notifs-bulk button:nth-child(2)").Click();
        cut.WaitForAssertion(() => Assert.Contains(errors, m => m.Contains("delete selected", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task MarkAllReadAsync_excludes_active_snoozed_deliveries()
    {
        using var t = new TestDb();
        var me = t.AddUser("U1");
        var client = t.AddNotificationClient("mail", "h:secret", null, null, 0, 1000, true);
        var now = DateTime.UtcNow;

        void Add(string title, DateTime? snoozedUntil)
        {
            var req = new NotificationRequest
            {
                Id = Guid.NewGuid(), ClientId = client.Id, IdempotencyKey = Guid.NewGuid().ToString(),
                Priority = "normal", EventType = "mail.new", Title = title, Body = "b",
                Status = NotificationStatus.Delivered, AcceptedAt = now, CreatedAt = now, UpdatedAt = now
            };
            t.Db.NotificationRequests.Add(req);
            t.Db.NotificationDeliveries.Add(new NotificationDelivery
            {
                Id = Guid.NewGuid(), RequestId = req.Id, UserId = me.Id,
                Status = NotificationStatus.Delivered, SnoozedUntil = snoozedUntil,
                DeliveredAt = now, CreatedAt = now, UpdatedAt = now
            });
        }
        Add("Normal unread", null);
        Add("Expired snooze", now.AddMinutes(-5));
        Add("Active snooze", now.AddHours(2));
        await t.Db.SaveChangesAsync();

        var svc = new NotificationGatewayService(t.Verify(), new TestHasher(), NullLogger<NotificationGatewayService>.Instance, new NoopBroadcaster());
        var updated = await svc.MarkAllReadAsync(me.Id, default);

        Assert.Equal(2, updated);
        var deliveries = await t.Verify().NotificationDeliveries.Include(d => d.Request).ToListAsync();
        Assert.Equal(NotificationStatus.Read, deliveries.Single(d => d.Request.Title == "Normal unread").Status);
        Assert.Equal(NotificationStatus.Read, deliveries.Single(d => d.Request.Title == "Expired snooze").Status);
        Assert.Equal(NotificationStatus.Delivered, deliveries.Single(d => d.Request.Title == "Active snooze").Status);
    }

    private sealed class TestHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string hash, string password) => hash == "h:" + password;
    }

    private sealed class NoopBroadcaster : INotificationRealtimeBroadcaster
    {
        public Task BroadcastAsync(IReadOnlyList<NotificationDeliveryState> deliveries, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void Search_change_clears_selectedIds_in_select_mode()
    {
        var r1 = Row("Alpha", 1);
        var r2 = Row("Beta", 2);
        var (ctx, _, _, _, _) = CreateContext(r1, r2);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".notifs-row").Count));

        cut.Find(".notifs-list-head button.nf-link").Click(); // Select mode
        cut.FindAll(".notifs-row")[0].Click();
        cut.FindAll(".notifs-row")[1].Click();
        Assert.Equal("2 selected", cut.Find(".notifs-list-title.center").TextContent);

        cut.Find(".notifs-search input").Input("Alpha");
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".notifs-row"));
            Assert.Equal("Select items", cut.Find(".notifs-list-title.center").TextContent);
            Assert.True(cut.Find(".notifs-bulk button:nth-child(2)").HasAttribute("disabled"));
        });
    }

    [Fact]
    public async Task Open_ticket_button_marks_read_and_Tickets_page_marks_read_by_target()
    {
        var ticketId = Guid.NewGuid();
        var r1 = Row("Ticket Assigned", 1, isRead: false, targetUrl: $"/tickets/{ticketId}", cat: "tickets");
        var (ctx, api, notifs, _, nav) = CreateContext(r1);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".notifs-row")));
        // First item is auto-selected in All view, still unread
        Assert.Contains("unread", cut.Find(".notifs-row").ClassList);

        cut.Find(".notifs-acts button").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.True(r1.IsRead);
            Assert.Equal(0, notifs.UnreadCount);
            Assert.EndsWith($"/tickets/{ticketId}", nav.Uri);
        });

        // Verify Tickets page calls MarkReadByTargetAsync("ticket", id) when unread > 0
        notifs.SetUnread(1);
        var tCut = ctx.Render<Tickets>(p => p.Add(x => x.Id, ticketId));
        tCut.WaitForAssertion(() => Assert.Contains(api.ReadByTargets, x => x.Kind == "ticket" && x.Id == ticketId));

        // Verify backend TargetPatterns matches both /tickets/{id} and /tickets/{id}#messages
        using var t = new TestDb();
        var me = t.AddUser("U2");
        var c = t.AddNotificationClient("tickets", "h:s", null, null, 0, 1000, true);
        void AddTicketNotif(string url)
        {
            var req = new NotificationRequest
            {
                Id = Guid.NewGuid(), ClientId = c.Id, IdempotencyKey = Guid.NewGuid().ToString(),
                Priority = "normal", EventType = "ticket.message_created", Title = "t", Body = "b", TargetUrl = url,
                Status = NotificationStatus.Delivered, AcceptedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            t.Db.NotificationRequests.Add(req);
            t.Db.NotificationDeliveries.Add(new NotificationDelivery
            {
                Id = Guid.NewGuid(), RequestId = req.Id, UserId = me.Id,
                Status = NotificationStatus.Delivered, DeliveredAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        }
        AddTicketNotif($"/tickets/{ticketId}");
        AddTicketNotif($"/tickets/{ticketId}#messages");
        await t.Db.SaveChangesAsync();

        Assert.Equal(2, await NotificationGatewayEndpoints.MarkReadByTargetCoreAsync(t.Verify(), me.Id, "ticket", ticketId, default));
    }

    [Fact]
    public void DeleteSelected_on_mobile_keeps_detail_open_when_delete_fails_and_closes_on_success()
    {
        var r1 = Row("Delete Me", 1);
        var (ctx, api, _, toasts, _) = CreateContext(r1);
        using var _ = ctx;

        ctx.JSInterop.Setup<bool>("ipop.mobile.isMobile").SetResult(true);
        ctx.JSInterop.Setup<bool>("ipop.mobile.pushDetail", _ => true).SetResult(true);

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".notifs-row")));

        // Tap row on mobile -> opens detail (.m-detail)
        cut.Find(".notifs-row").Click();
        cut.WaitForAssertion(() => Assert.Contains("m-detail", cut.Find(".notifs-v6").ClassList));

        // Fail DELETE -> detail stays open and error toast appears
        api.FailNextAction = HttpStatusCode.InternalServerError;
        cut.Find(".notifs-dt-bar button.danger").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains(toasts.Active, t => t.Kind == ToastKind.Error && t.Message.Contains("delete", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("m-detail", cut.Find(".notifs-v6").ClassList);
        });

        // Succeed DELETE -> detail closes
        api.FailNextAction = HttpStatusCode.OK;
        cut.Find(".notifs-dt-bar button.danger").Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("m-detail", cut.Find(".notifs-v6").ClassList));
    }

    [Fact]
    public async Task Realtime_sync_refreshes_when_unread_unchanged_and_PreferencesChanged_updates_DND_sidebar()
    {
        var r1 = Row("Initial Item", 2, isRead: false);
        var (ctx, api, notifs, _, _) = CreateContext(r1);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal("Initial Item", cut.Find(".notifs-row-title").TextContent));
        Assert.Equal("Off", cut.Find(".notifs-dnd-txt small").TextContent);

        // Simulate server state where 1 old item was read and 1 new unread item arrived -> total UnreadCount still 1!
        r1.IsRead = true;
        var r2 = Row("Replacement Unread", 0, isRead: false);
        api.Rows.Add(r2);
        notifs.SetUnread(1); // prime UnreadCount = 1 so LoadRecentAsync sees no change in UnreadCount
        await cut.InvokeAsync(() => notifs.LoadRecentAsync());

        cut.WaitForAssertion(() =>
        {
            var titles = cut.FindAll(".notifs-row-title").Select(e => e.TextContent).ToList();
            Assert.Contains("Replacement Unread", titles);
        });

        // Simulate Settings Dialog saving DND -> Notifs.LoadPreferencesAsync() updates sidebar DND switch
        api.QuietHoursEnabled = true;
        await cut.InvokeAsync(() => notifs.LoadPreferencesAsync());
        cut.WaitForAssertion(() => Assert.Contains("on", cut.Find(".notifs-dnd button[role='switch']").ClassList));
    }

    [Fact]
    public async Task Cursor_pagination_in_Unread_never_skips_items_when_earlier_items_are_read()
    {
        using var t = new TestDb();
        var me = t.AddUser("U3");
        var c = t.AddNotificationClient("mail", "h:s", null, null, 0, 1000, true);
        var baseTime = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

        var deliveries = new List<NotificationDelivery>();
        for (var i = 0; i < 6; i++)
        {
            var req = new NotificationRequest
            {
                Id = Guid.NewGuid(), ClientId = c.Id, IdempotencyKey = $"k{i}",
                Priority = "normal", EventType = "mail.new", Title = $"N{i}", Body = "b",
                Status = NotificationStatus.Delivered, AcceptedAt = baseTime, CreatedAt = baseTime, UpdatedAt = baseTime
            };
            t.Db.NotificationRequests.Add(req);
            var d = new NotificationDelivery
            {
                Id = Guid.NewGuid(), RequestId = req.Id, UserId = me.Id,
                Status = NotificationStatus.Delivered, DeliveredAt = baseTime.AddMinutes(-i),
                CreatedAt = baseTime, UpdatedAt = baseTime
            };
            t.Db.NotificationDeliveries.Add(d);
            deliveries.Add(d);
        }
        await t.Db.SaveChangesAsync();

        // Page 1 (size 3): N0, N1, N2
        var p1 = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, "unread", 1, 3, default);
        Assert.Equal(["N0", "N1", "N2"], p1.Items.Select(x => x.Title).ToList());
        Assert.True(p1.HasMore);

        // User reads N0 & N1 on page 1 before clicking Show older notifications
        await using (var db2 = t.Verify())
        {
            var toRead = await db2.NotificationDeliveries.Where(d => d.RequestId == p1.Items[0].Id || d.RequestId == p1.Items[1].Id).ToListAsync();
            foreach (var d in toRead) d.Status = NotificationStatus.Read;
            await db2.SaveChangesAsync();
        }

        // Cursor from last item of p1 (N2) must return N3, N4, N5 without skipping N3 or N4!
        var last = p1.Items[^1];
        var p2 = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, "unread", 1, 3, default, beforeAt: last.At, beforeId: last.Id);
        Assert.Equal(["N3", "N4", "N5"], p2.Items.Select(x => x.Title).ToList());
        Assert.False(p2.HasMore);

        // Same-timestamp tie-breaking across pages must not skip or duplicate
        var tieTime = baseTime.AddHours(1);
        for (var j = 0; j < 4; j++)
        {
            var req = new NotificationRequest
            {
                Id = Guid.NewGuid(), ClientId = c.Id, IdempotencyKey = $"tie{j}",
                Priority = "normal", EventType = "mail.new", Title = $"Tie{j}", Body = "b",
                Status = NotificationStatus.Delivered, AcceptedAt = tieTime, CreatedAt = tieTime, UpdatedAt = tieTime
            };
            t.Db.NotificationRequests.Add(req);
            t.Db.NotificationDeliveries.Add(new NotificationDelivery
            {
                Id = Guid.NewGuid(), RequestId = req.Id, UserId = me.Id,
                Status = NotificationStatus.Delivered, DeliveredAt = tieTime,
                CreatedAt = tieTime, UpdatedAt = tieTime
            });
        }
        await t.Db.SaveChangesAsync();

        var tp1 = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, "unread", 1, 2, default);
        Assert.Equal(2, tp1.Items.Count);
        var tp2 = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, "unread", 1, 2, default, beforeAt: tp1.Items[^1].At, beforeId: tp1.Items[^1].Id);
        Assert.Equal(2, tp2.Items.Count);
        var tieTitles = tp1.Items.Concat(tp2.Items).Select(x => x.Title).ToList();
        Assert.Equal(4, tieTitles.Distinct().Count());
        Assert.All(tieTitles, title => Assert.StartsWith("Tie", title));
    }

    [Fact]
    public void KeepPages_and_LiveRefresh_preserve_more_than_100_loaded_items()
    {
        var baseTime = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 125)
            .Select(i => Row($"Item {i:D3}", i, isRead: false, at: baseTime.AddMinutes(-i)))
            .ToArray();

        var (ctx, api, notifs, _, _) = CreateContext(rows);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Equal(50, cut.FindAll(".notifs-row").Count));

        // Load page 2 (100 items) and page 3 (125 items)
        cut.Find("button.notifs-more").Click();
        cut.WaitForAssertion(() => Assert.Equal(100, cut.FindAll(".notifs-row").Count));
        cut.Find("button.notifs-more").Click();
        cut.WaitForAssertion(() => Assert.Equal(125, cut.FindAll(".notifs-row").Count));

        // Delete 1 item -> ReloadAsync(keepPages: true) keeps all 124 remaining items (not cut to 100!)
        cut.FindAll(".notifs-row")[0].QuerySelector("button[aria-label='Delete']")!.Click();
        cut.WaitForAssertion(() => Assert.Equal(124, cut.FindAll(".notifs-row").Count));

        // Realtime update adds 1 item -> LiveRefreshAsync keeps all 125 items (not cut to 100!)
        api.Rows.Add(Row("Newest Live", 0, isRead: false, at: baseTime.AddMinutes(5)));
        cut.InvokeAsync(() => notifs.SetUnread(125));
        cut.WaitForAssertion(() => Assert.Equal(125, cut.FindAll(".notifs-row").Count));
    }
}
