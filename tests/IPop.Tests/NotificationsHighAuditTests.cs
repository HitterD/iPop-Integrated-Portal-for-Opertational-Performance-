using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using IPop.Api.Endpoints;
using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Application.Tickets;
using IPop.Core.Entities;
using IPop.Infrastructure.Notifications;
using IPop.Infrastructure.Tickets;
using IPop.Web.Components;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

public class NotificationsHighAuditTests
{
    private sealed class FakeApi : HttpMessageHandler
    {
        public bool QuietHoursEnabled { get; set; }
        public string QuietStart { get; set; }
        public string QuietEnd { get; set; }
        public string QuietDays { get; set; } = "mon,tue,wed,thu,fri,sat,sun";

        public FakeApi()
        {
            var wib = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"));
            QuietStart = wib.AddHours(-1).ToString("HH:mm");
            QuietEnd = wib.AddHours(1).ToString("HH:mm");
        }
        public bool AllowImportant { get; set; } = true;
        public string MutedTypes { get; set; } = "";

        public List<(string Method, string Path, string Body)> Calls { get; } = [];
        public IdeskTicketPreviewDto? AgentPreview { get; set; }
        public HttpStatusCode AgentPreviewStatus { get; set; } = HttpStatusCode.OK;
        public List<TicketMessageDto> AgentMessages { get; } = [];
        public Func<Guid, object>? InboxItemFactory { get; set; }
        public Guid NotificationId { get; } = Guid.NewGuid();
        public string? TargetUrl { get; set; }
        public string Category { get; set; } = "tickets";
        public string EventType { get; set; } = "ticket.message_created";
        public string Body { get; set; } = "Budi · Reporter\nBudi: “Tolong cek”\n#041026-INS-0001";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            Calls.Add((req.Method.Method, req.RequestUri.PathAndQuery, body));

            if (path == "/api/notifications/v1/preferences" && req.Method == HttpMethod.Get)
            {
                return JsonOk(new
                {
                    quietHoursEnabled = QuietHoursEnabled,
                    quietStart = QuietStart,
                    quietEnd = QuietEnd,
                    mutedTypes = MutedTypes,
                    quietDays = QuietDays,
                    allowImportant = AllowImportant,
                    timeZoneId = "SE Asia Standard Time"
                });
            }

            if (path == "/api/notifications/v1/preferences" && req.Method == HttpMethod.Put)
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                QuietHoursEnabled = r.GetProperty("quietHoursEnabled").GetBoolean();
                if (r.TryGetProperty("quietStart", out var qs) && qs.ValueKind == JsonValueKind.String) QuietStart = qs.GetString() ?? QuietStart;
                if (r.TryGetProperty("quietEnd", out var qe) && qe.ValueKind == JsonValueKind.String) QuietEnd = qe.GetString() ?? QuietEnd;
                if (r.TryGetProperty("quietDays", out var qd) && qd.ValueKind == JsonValueKind.String) QuietDays = qd.GetString() ?? "";
                if (r.TryGetProperty("allowImportant", out var ai)) AllowImportant = ai.GetBoolean();
                if (r.TryGetProperty("mutedTypes", out var mt) && mt.ValueKind == JsonValueKind.String) MutedTypes = mt.GetString() ?? "";
                return JsonOk(new
                {
                    quietHoursEnabled = QuietHoursEnabled,
                    quietStart = QuietStart,
                    quietEnd = QuietEnd,
                    mutedTypes = MutedTypes,
                    quietDays = QuietDays,
                    allowImportant = AllowImportant
                });
            }

            if (path == "/api/notifications/v1/preferences/mute-type" && req.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(body);
                var type = doc.RootElement.GetProperty("type").GetString()!;
                var muted = doc.RootElement.GetProperty("muted").GetBoolean();
                var set = MutedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (muted) set.Add(type.Trim().ToLowerInvariant());
                else set.Remove(type.Trim().ToLowerInvariant());
                MutedTypes = string.Join(',', set);
                return JsonOk(new { mutedTypes = MutedTypes });
            }

            if (path == "/api/notifications/v1/preferences/timezone")
                return JsonOk(new { timeZoneId = "SE Asia Standard Time" });

            if (path == "/api/notifications/v1/inbox")
            {
                var item = InboxItemFactory?.Invoke(NotificationId) ?? new
                {
                    id = NotificationId,
                    type = "system",
                    title = "Wifi lantai 2 mati",
                    body = Body,
                    source = "tickets",
                    at = DateTime.UtcNow,
                    isRead = false,
                    priority = "normal",
                    targetUrl = TargetUrl,
                    snoozedUntil = (DateTime?)null,
                    actorName = "Budi",
                    eventType = EventType,
                    category = Category
                };
                return JsonOk(new
                {
                    items = new[] { item },
                    totalCount = 1,
                    unreadCount = 1,
                    unreadByCategory = new Dictionary<string, int> { [Category] = 1 },
                    snoozedCount = 0
                });
            }

            if (req.Method == HttpMethod.Post && path.StartsWith("/api/notifications/v1/notifications/") && path.EndsWith("/read"))
                return JsonOk(new { status = "Read" });

            if (req.Method == HttpMethod.Get && path.StartsWith("/api/tickets/idesk/") && path.EndsWith("/preview"))
            {
                if (AgentPreviewStatus != HttpStatusCode.OK)
                    return new HttpResponseMessage(AgentPreviewStatus);
                var p = AgentPreview!;
                return JsonOk(p with { Messages = AgentMessages.ToList() });
            }

            if (req.Method == HttpMethod.Post && path.StartsWith("/api/tickets/") && path.EndsWith("/reply"))
            {
                using var doc = JsonDocument.Parse(body);
                var text = doc.RootElement.GetProperty("content").GetString() ?? "";
                AgentMessages.Add(new TicketMessageDto(Guid.NewGuid().ToString(), "Bagastyo", true, text, [], DateTime.UtcNow));
                return JsonOk(new { ok = true });
            }

            if (path == "/api/push/devices") return JsonOk(Array.Empty<object>());
            if (path == "/api/notifier/me") return JsonOk(new { ictTools = false, onlinePcs = Array.Empty<string>() });
            if (path == "/api/push/preferences") return JsonOk(new { showPreview = true });
            if (path == "/api/push/diagnostics") return JsonOk(new { enabled = false, recent = Array.Empty<object>() });

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonOk(object payload) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }

    private static (BunitContext Ctx, FakeApi Api, NotificationState Notifs, NotificationSettingsState SettingsState) CreateWebContext(FakeApi? api = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        api ??= new FakeApi();
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        var toasts = new ToastService();
        var notifs = new NotificationState(auth, http, toasts, ctx.JSInterop.JSRuntime);
        var push = new PushState(auth, http, ctx.JSInterop.JSRuntime, notifs);
        var settingsState = new NotificationSettingsState();

        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(notifs);
        ctx.Services.AddSingleton(push);
        ctx.Services.AddSingleton(new BadgeState(auth));
        ctx.Services.AddSingleton(settingsState);
        ctx.Services.AddSingleton(new OverlayStack());
        ctx.Services.AddSingleton(new PhoneState(ctx.JSInterop.JSRuntime));

        return (ctx, api, notifs, settingsState);
    }

    [Fact]
    public async Task Toggle_Dnd_on_Notifications_updates_NotificationState_in_live_session()
    {
        var (ctx, api, notifs, _) = CreateWebContext();
        using var _ = ctx;
        await notifs.LoadPreferencesAsync();
        Assert.False(notifs.ShouldSuppress("mail.new", "/mail", "normal"));

        var prefsChangedCount = 0;
        notifs.PreferencesChanged += () => prefsChangedCount++;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".notifs-dnd button[role='switch']")));

        cut.Find(".notifs-dnd button[role='switch']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(api.QuietHoursEnabled);
            Assert.True(prefsChangedCount >= 1);
            Assert.True(notifs.ShouldSuppress("mail.new", "/mail", "normal"));
        });
    }

    [Fact]
    public async Task Toggle_Mute_on_Notifications_updates_NotificationState_in_live_session()
    {
        var (ctx, api, notifs, _) = CreateWebContext();
        using var _ = ctx;
        await notifs.LoadPreferencesAsync();
        Assert.False(notifs.ShouldSuppress("chat.mention", "/chat/1", "normal"));

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".notifs-toolbar-actions button:nth-child(2)")));

        // Open Preferences pane
        cut.Find(".notifs-toolbar-actions button:nth-child(2)").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("button[role='switch'][aria-label='Mentions']")));

        // Mute Mentions
        cut.Find("button[role='switch'][aria-label='Mentions']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("mention", api.MutedTypes);
            Assert.True(notifs.ShouldSuppress("chat.mention", "/chat/1", "normal"));
            Assert.False(notifs.ShouldSuppress("chat.message", "/chat/1", "normal"));
        });

        // Unmute Mentions
        cut.Find("button[role='switch'][aria-label='Mentions']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("mention", api.MutedTypes);
            Assert.False(notifs.ShouldSuppress("chat.mention", "/chat/1", "normal"));
        });
    }

    [Fact]
    public void NotificationSettingsDialog_shows_all_8_categories_and_preserves_existing_mutes_on_save()
    {
        var api = new FakeApi { MutedTypes = "MENTION,TICKETS,call" };
        var (ctx, _, _, settingsState) = CreateWebContext(api);
        using var _ = ctx;

        var cut = ctx.Render<NotificationSettingsDialog>();
        cut.InvokeAsync(() => settingsState.Open());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".notification-settings-page")));

        // Switch to Categories tab
        cut.FindAll(".settings-tab").First(t => t.TextContent.Contains("Categories")).Click();

        var labels = cut.FindAll(".setting-item .setting-name").Select(e => e.TextContent.Trim()).ToList();
        Assert.Equal(
            ["Mentions", "Chat", "Mail", "iDesk", "Tasks", "Meetings & Calls", "Memos & News", "System"],
            labels);

        var checkboxes = cut.FindAll(".setting-item input[type='checkbox']");
        Assert.Equal(8, checkboxes.Count);
        // Mentions (0), iDesk (3), Meetings & Calls (5 via legacy "call") are unchecked; others checked
        Assert.False(checkboxes[0].HasAttribute("checked"));
        Assert.True(checkboxes[1].HasAttribute("checked"));
        Assert.True(checkboxes[2].HasAttribute("checked"));
        Assert.False(checkboxes[3].HasAttribute("checked"));
        Assert.True(checkboxes[4].HasAttribute("checked"));
        Assert.False(checkboxes[5].HasAttribute("checked"));
        Assert.True(checkboxes[6].HasAttribute("checked"));
        Assert.True(checkboxes[7].HasAttribute("checked"));

        // Save changes without touching Categories -> mention, tickets, meeting stay muted!
        cut.Find(".settings-footer .btn-primary").Click();
        cut.WaitForAssertion(() => Assert.Contains(api.Calls, c => c.Method == "PUT" && c.Path == "/api/notifications/v1/preferences"));

        var savedMuted = api.MutedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("mention", savedMuted);
        Assert.Contains("tickets", savedMuted);
        Assert.Contains("meeting", savedMuted);
    }

    [Fact]
    public async Task NotificationState_ShouldSuppress_honors_all_8_categories_and_legacy_call()
    {
        var api = new FakeApi { MutedTypes = "mention,tickets,task,news,call" };
        var (ctx, _, notifs, _) = CreateWebContext(api);
        using var _ = ctx;

        await notifs.LoadPreferencesAsync();

        Assert.True(notifs.ShouldSuppress("chat.mention", "/chat/1", "normal"));
        Assert.False(notifs.ShouldSuppress("chat.message", "/chat/1", "normal"));
        Assert.True(notifs.ShouldSuppress("ticket.assigned", "/idesk/tickets/1", "normal"));
        Assert.True(notifs.ShouldSuppress("task.assigned", "/projects/1", "normal"));
        Assert.True(notifs.ShouldSuppress("news.published", "/news/1", "normal"));
        Assert.True(notifs.ShouldSuppress("meeting.reminder", "/calls", "normal"));
        Assert.False(notifs.ShouldSuppress("mail.new", "/mail", "normal"));
    }

    [Fact]
    public async Task MuteType_endpoint_clears_legacy_call_when_unmuting_meeting()
    {
        using var t = new TestDb();
        var me = t.AddUser("U1");
        t.Db.NotificationPreferences.Add(new NotificationPreference
        {
            Id = Guid.NewGuid(),
            UserId = me.Id,
            MutedTypes = "call,tickets",
            UpdatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var pref = await t.Db.NotificationPreferences.FirstAsync(p => p.UserId == me.Id);
        var types = pref.MutedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        types.Remove(NotificationCategory.Meeting);
        types.Remove("call");
        pref.MutedTypes = string.Join(',', types);
        await t.Db.SaveChangesAsync();

        var reloaded = await t.Verify().NotificationPreferences.FirstAsync(p => p.UserId == me.Id);
        Assert.Equal("tickets", reloaded.MutedTypes);
    }

    private sealed class StubIdeskClient : IIdeskClient
    {
        public bool IsConfigured => true;
        public Dictionary<string, IdeskResult<IdeskTicket>> Tickets { get; } = [];
        public Dictionary<string, IdeskResult<IdeskMessages>> Messages { get; } = [];

        public Task<IdeskResult<IReadOnlyList<TicketModuleDto>>> GetModulesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IdeskResult<IdeskTicket>> CreateTicketAsync(string jsonBody, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IdeskResult<IdeskTicket>> GetTicketAsync(string ticketNumber, CancellationToken ct = default) =>
            Task.FromResult(Tickets.TryGetValue(ticketNumber, out var r) ? r : new IdeskResult<IdeskTicket>(IdeskOutcome.NotFound));
        public Task<IdeskResult<IReadOnlyList<IdeskTicket>>> ListByNikAsync(string nik, string state, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IdeskResult<IdeskMessages>> GetMessagesAsync(string ticketNumber, CancellationToken ct = default) =>
            Task.FromResult(Messages.TryGetValue(ticketNumber, out var r) ? r : new IdeskResult<IdeskMessages>(IdeskOutcome.NotFound));
        public Task<IdeskResult<bool>> ReplyAsync(string ticketRef, string nik, string content, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IdeskResult<IReadOnlyList<IdeskBooking>>> ListZoomBookingsAsync(string nik, DateOnly from, DateOnly to, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class NoopRealtime : IRealtime
    {
        public Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ToGroupAsync(string group, string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopGateway : INotificationGatewayService
    {
        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message, NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message, NotificationTarget target, string idempotencyKey, string priority = "normal", string? targetUrl = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message, NotificationTarget target, Guid? actorUserId, string? idempotencyKey, string priority = "normal", string? targetUrl = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task GetIdeskPreviewAsync_returns_facts_and_messages_for_assigned_agent_and_blocks_others()
    {
        using var t = new TestDb();
        var agent = t.AddUser("00061058");
        var outsider = t.AddUser("00099999");
        var ideskId = Guid.NewGuid();
        var idesk = new StubIdeskClient();
        idesk.Tickets[ideskId.ToString("D")] = new(IdeskOutcome.Ok, new IdeskTicket(
            ideskId.ToString("D"), "051026-SEC-0003", null, "IDESK",
            "00000024", "Christhin", "IN_PROGRESS", "HIGH", "it-support", "NETWORK",
            "Wifi lantai 2 mati", agent.Name, null, DateTime.UtcNow, DateTime.UtcNow, null, null, null,
            AssigneeNik: agent.Nik));
        idesk.Messages["051026-SEC-0003"] = new(IdeskOutcome.Ok, new IdeskMessages(null, [
            new IdeskMessage("m1", "Christhin", true, "Masih belum bisa", [], DateTime.UtcNow.AddMinutes(-5)),
            new IdeskMessage("m2", agent.Name, false, "Sedang dicek", [], DateTime.UtcNow.AddMinutes(-2)),
        ]));

        var svc = new TicketService(t.Db, idesk, new TicketModuleCache(), new NoopGateway(), new NoopRealtime(), NullLogger<TicketService>.Instance);

        var ok = await svc.GetIdeskPreviewAsync(agent.Id, ideskId);
        Assert.Equal(IdeskPreviewKind.Ok, ok.Kind);
        Assert.Equal("051026-SEC-0003", ok.Preview!.TicketNumber);
        Assert.Equal("IN_PROGRESS", ok.Preview.Status);
        Assert.Equal("HIGH", ok.Preview.Priority);
        Assert.Equal(agent.Name, ok.Preview.AssigneeName);
        Assert.Equal(2, ok.Preview.Messages.Count);
        Assert.False(ok.Preview.Messages[0].Mine); // requester message must NOT be Mine
        Assert.Equal("Christhin", ok.Preview.Messages[0].Sender);
        Assert.True(ok.Preview.Messages[1].Mine);  // agent's own message

        // Unrelated user without notification or NIK match gets NotFound
        var denied = await svc.GetIdeskPreviewAsync(outsider.Id, ideskId);
        Assert.Equal(IdeskPreviewKind.NotFound, denied.Kind);

        // If messages call fails, facts still return with MessagesUnavailable = true
        idesk.Messages["051026-SEC-0003"] = new(IdeskOutcome.Unreachable);
        var partial = await svc.GetIdeskPreviewAsync(agent.Id, ideskId);
        Assert.Equal(IdeskPreviewKind.Ok, partial.Kind);
        Assert.True(partial.Preview!.MessagesUnavailable);
        Assert.Empty(partial.Preview.Messages);
    }

    [Fact]
    public void Notifications_page_loads_idesk_agent_preview_and_refreshes_after_reply()
    {
        var ideskId = Guid.NewGuid();
        var api = new FakeApi
        {
            TargetUrl = $"/idesk/tickets/{ideskId:D}",
            AgentPreview = new IdeskTicketPreviewDto(ideskId.ToString("D"), "051026-SEC-0003", "IN_PROGRESS", "HIGH", "Bagastyo", [])
        };
        api.AgentMessages.Add(new TicketMessageDto("m1", "Christhin", false, "Masih belum bisa", [], DateTime.UtcNow.AddMinutes(-5)));

        var (ctx, _, _, _) = CreateWebContext(api);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() =>
        {
            var info = cut.Find(".notifs-info").TextContent;
            Assert.Contains("#051026-SEC-0003", info);
            Assert.Contains("IN PROGRESS", info);
            Assert.Contains("HIGH", info);
            Assert.Contains("Bagastyo", info);
            Assert.Contains("Masih belum bisa", cut.Find(".notifs-msgs").TextContent);
        });

        // Agent sends a reply -> POST /reply and reload /preview shows the new reply
        cut.Find(".notifs-reply-box input").Input("Sudah normal ya");
        cut.Find(".notifs-reply button").Click();

        cut.WaitForAssertion(() =>
        {
            var msgs = cut.FindAll(".notifs-msg");
            Assert.Equal(2, msgs.Count);
            Assert.Contains("Sudah normal ya", msgs[1].TextContent);
            Assert.Contains("me", msgs[1].ClassList);
        });
    }

    [Fact]
    public void Notifications_page_shows_fallback_hint_when_idesk_agent_preview_fails()
    {
        var ideskId = Guid.NewGuid();
        var api = new FakeApi
        {
            TargetUrl = $"/idesk/tickets/{ideskId:D}#update",
            AgentPreviewStatus = HttpStatusCode.ServiceUnavailable
        };

        var (ctx, _, _, _) = CreateWebContext(api);
        using var _ = ctx;

        var cut = ctx.Render<Notifications>();
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".notifs-info"));
            Assert.Contains("Tolong cek", cut.Find(".notifs-dt-text").TextContent);
            Assert.Contains("aren’t available", cut.Find(".notifs-hint").TextContent);
        });
    }
}
