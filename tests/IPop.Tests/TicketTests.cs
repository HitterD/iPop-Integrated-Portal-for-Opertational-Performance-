using System.Net;
using System.Text;
using System.Text.Json;
using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Application.Tickets;
using IPop.Core.Entities;
using IPop.Infrastructure.Badges;
using IPop.Infrastructure.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// ICT tickets via iDesk (docs/specs/idesk-tickets.md): signing, create outcomes, outbox,
/// webhook matching/dedupe, sync soft-delete, badge and per-user isolation.
public class TicketTests
{
    private const string Secret = "s3cr3t-s3cr3t-s3cr3t-s3cr3t-s3cr3t";

    // ── Fakes ─────────────────────────────────────────────────────────────

    private sealed class FakeIdesk : IIdeskClient
    {
        public bool IsConfigured { get; set; } = true;
        public List<string> Posted { get; } = [];
        public Queue<IdeskResult<IdeskTicket>> CreateResults { get; } = new();
        public IdeskResult<IReadOnlyList<IdeskTicket>> ListResult { get; set; } = new(IdeskOutcome.Ok, []);
        public Dictionary<string, IdeskResult<IdeskTicket>> ByNumber { get; } = [];
        public List<TicketModuleDto> Modules { get; } =
        [
            new("it-support", "IT Support Tickets", null, ["GENERAL", "HARDWARE", "NETWORK", "HARDWARE_INSTALLATION"], null,
                ["LOW", "MEDIUM", "HIGH", "CRITICAL"], true),
            new("oracle-k2", "Oracle K2 Request", null, ["ORACLE_REQUEST"], "ORACLE_REQUEST", ["LOW", "MEDIUM", "HIGH", "CRITICAL"], false),
        ];

        public Task<IdeskResult<IReadOnlyList<TicketModuleDto>>> GetModulesAsync(CancellationToken ct = default) =>
            Task.FromResult(new IdeskResult<IReadOnlyList<TicketModuleDto>>(IdeskOutcome.Ok, Modules));

        public Task<IdeskResult<IdeskTicket>> CreateTicketAsync(string jsonBody, CancellationToken ct = default)
        {
            Posted.Add(jsonBody);
            if (CreateResults.Count > 0) return Task.FromResult(CreateResults.Dequeue());
            using var doc = JsonDocument.Parse(jsonBody);
            var r = doc.RootElement;
            return Task.FromResult(new IdeskResult<IdeskTicket>(IdeskOutcome.Ok, Remote(
                r.GetProperty("externalRef").GetString(), r.GetProperty("requester").GetProperty("nik").GetString()!,
                title: r.GetProperty("title").GetString()!) with
            {
                Module = r.GetProperty("module").GetString()!,
                Category = r.TryGetProperty("category", out var c) ? c.GetString() : null,
            }));
        }

        public Task<IdeskResult<IdeskTicket>> GetTicketAsync(string ticketNumber, CancellationToken ct = default) =>
            Task.FromResult(ByNumber.TryGetValue(ticketNumber, out var r) ? r : new IdeskResult<IdeskTicket>(IdeskOutcome.NotFound));

        public Task<IdeskResult<IReadOnlyList<IdeskTicket>>> ListByNikAsync(string nik, string state, CancellationToken ct = default) =>
            Task.FromResult(ListResult);
    }

    private sealed class RecordingGateway : INotificationGatewayService
    {
        public List<(string Type, string Title, string Message, Guid? To, string? Url)> Sent { get; } = [];

        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Assert.Equal("tickets", clientSlug);
            Sent.Add((eventType, title, message, target.UserId, targetUrl));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }

        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class RecordingRealtime : IRealtime
    {
        public List<(string Nik, string Evt)> Sent { get; } = [];
        public Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default) { Sent.Add((nik, evt)); return Task.CompletedTask; }
        public Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ToGroupAsync(string group, string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed record Rig(TestDb T, FakeIdesk Idesk, RecordingGateway Gw, RecordingRealtime Rt, TicketService Svc);

    private static Rig Setup()
    {
        var t = new TestDb();
        var idesk = new FakeIdesk();
        var gw = new RecordingGateway();
        var rt = new RecordingRealtime();
        return new Rig(t, idesk, gw, rt, new TicketService(t.Db, idesk, new TicketModuleCache(), gw, rt, NullLogger<TicketService>.Instance));
    }

    private static int _seq;

    private static IdeskTicket Remote(string? externalRef, string nik, string status = TicketStatus.Todo, string? id = null,
        string? number = null, string? assignee = null, string title = "Wifi lantai 2 mati", DateTime? createdAt = null, DateTime? updatedAt = null)
    {
        var n = Interlocked.Increment(ref _seq);
        var created = createdAt ?? DateTime.UtcNow.AddMinutes(-5);
        return new IdeskTicket(id ?? Guid.NewGuid().ToString(), number ?? $"041026-INS-{n:D4}", externalRef,
            externalRef is null ? "IDESK" : "IPOP", nik, "Budi", status, "HIGH", "it-support", "NETWORK", title, assignee,
            null, created, updatedAt ?? created, null, null, $"https://idesk.example/client/tickets/{id}");
    }

    private static CreateTicketRequest Req(string title = "Wifi lantai 2 mati", string module = "it-support", string? category = "NETWORK",
        string priority = "MEDIUM", IReadOnlyList<NewTicketAttachment>? attachments = null, string? criticalReason = null,
        string? hardwareType = null, string? date = null, string? time = null) =>
        new(module, category, priority, title, "Tidak bisa konek sejak pagi.", criticalReason, "Laptop", null, hardwareType, date, time, attachments);

    private static string Webhook(string id, string evt, IdeskTicket t, string? previousStatus = null) =>
        JsonSerializer.Serialize(new
        {
            id, @event = evt, occurredAt = DateTime.UtcNow.ToString("O"), previousStatus,
            ticket = new
            {
                ticketId = t.TicketId, ticketNumber = t.TicketNumber, externalRef = t.ExternalRef, origin = t.Origin,
                requester = new { nik = t.RequesterNik, name = t.RequesterName }, status = t.Status, priority = t.Priority,
                module = t.Module, category = t.Category, title = t.Title,
                assignee = t.AssigneeName is null ? null : new { name = t.AssigneeName },
                slaTarget = (string?)null, createdAt = t.CreatedAt.ToString("O"), updatedAt = t.UpdatedAt.ToString("O"),
                resolvedAt = (string?)null, closedAt = (string?)null, url = t.Url,
            },
        });

    // ── Signing ───────────────────────────────────────────────────────────

    [Fact]
    public void Signature_matches_known_vector()
    {
        // Computed independently: python hmac.new(secret, b'1700000000000.{"a":1}', sha256).hexdigest()
        Assert.Equal("c2ce878d1f2be6e283756b9cb5e9c5e42647af73e2b00e44fc057e0ed377ade2",
            IdeskSignature.Sign(Secret, "1700000000000", "{\"a\":1}"));
    }

    [Fact]
    public void Webhook_verification_rejects_bad_signature_and_stale_timestamp()
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
        const string body = "{\"a\":1}";
        var sig = IdeskSignature.Sign(Secret, "1700000000000", body);

        Assert.True(IdeskSignature.Verify(Secret, "1700000000000", sig, body, now));
        Assert.True(IdeskSignature.Verify(Secret, "1700000000000", sig, body, now.AddMinutes(4)));
        Assert.False(IdeskSignature.Verify(Secret, "1700000000000", sig, body, now.AddMinutes(6)));      // too old
        Assert.False(IdeskSignature.Verify(Secret, "1700000000000", sig, body, now.AddMinutes(-6)));     // from the future
        Assert.False(IdeskSignature.Verify(Secret, "1700000000000", sig, "{\"a\":2}", now));              // body tampered
        Assert.False(IdeskSignature.Verify(Secret, "1700000000000", sig[..^1] + "0", body, now));
        Assert.False(IdeskSignature.Verify("another-secret-another-secret-xx", "1700000000000", sig, body, now));
        Assert.False(IdeskSignature.Verify(Secret, null, sig, body, now));
        Assert.False(IdeskSignature.Verify(Secret, "17e11", sig, body, now));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (HttpIdeskClient, StubHandler) Client(HttpStatusCode status, string body, string secret = Secret)
    {
        var h = new StubHandler(status, body);
        var opts = new IdeskOptions { BaseUrl = "https://idesk.test/api/v1/integrations/ipop/", ClientId = "ipop", ClientSecret = secret };
        return (new HttpIdeskClient(new HttpClient(h), opts, NullLogger<HttpIdeskClient>.Instance), h);
    }

    [Fact]
    public async Task Client_signs_the_exact_bytes_it_sends()
    {
        var (c, h) = Client(HttpStatusCode.Created, "{\"ticketId\":\"t1\",\"ticketNumber\":\"N1\",\"status\":\"TODO\",\"title\":\"x\"}");
        const string body = "{\"externalRef\":\"IPOP-ICT-2026-000001\"}";
        var r = await c.CreateTicketAsync(body);
        Assert.True(r.Ok);
        Assert.Equal("N1", r.Value!.TicketNumber);
        var (req, sent) = Assert.Single(h.Seen);
        Assert.Equal(body, sent);
        Assert.Equal("https://idesk.test/api/v1/integrations/ipop/tickets", req.RequestUri!.ToString());
        var ts = req.Headers.GetValues("x-ipop-timestamp").Single();
        Assert.Equal("ipop", req.Headers.GetValues("x-ipop-client-id").Single());
        Assert.Equal(IdeskSignature.Sign(Secret, ts, body), req.Headers.GetValues("x-ipop-signature").Single());

        var (g, gh) = Client(HttpStatusCode.OK, "[]");
        await g.ListByNikAsync("00000024", "all");
        var (greq, _) = Assert.Single(gh.Seen);
        Assert.Equal(IdeskSignature.Sign(Secret, greq.Headers.GetValues("x-ipop-timestamp").Single(), ""),
            greq.Headers.GetValues("x-ipop-signature").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, IdeskOutcome.Rejected)]
    [InlineData(HttpStatusCode.UnprocessableEntity, IdeskOutcome.NotRegistered)]
    [InlineData(HttpStatusCode.NotFound, IdeskOutcome.NotFound)]
    [InlineData(HttpStatusCode.BadGateway, IdeskOutcome.Unreachable)]
    [InlineData(HttpStatusCode.Unauthorized, IdeskOutcome.Unreachable)]
    public async Task Client_maps_status_codes(HttpStatusCode status, IdeskOutcome expected)
    {
        var (c, _) = Client(status, "{\"message\":[\"title must be longer\"]}");
        var r = await c.CreateTicketAsync("{}");
        Assert.Equal(expected, r.Outcome);
        if (expected == IdeskOutcome.Rejected) Assert.Equal("title must be longer", r.Message);
    }

    [Fact]
    public async Task Client_fails_closed_without_a_long_secret()
    {
        var (c, h) = Client(HttpStatusCode.OK, "[]", secret: "too-short");
        Assert.False(c.IsConfigured);
        Assert.Equal(IdeskOutcome.NotConfigured, (await c.GetModulesAsync()).Outcome);
        Assert.Empty(h.Seen);
    }

    // ── Create ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_accepted_returns_created_and_drops_the_payload()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("00000024");
        var file = new NewTicketAttachment("screen.png", Convert.ToBase64String(new byte[1234]));

        var r = await x.Svc.CreateAsync(u.Id, Req(attachments: [file]));

        Assert.Equal(TicketCreateKind.Created, r.Kind);
        Assert.Equal(TicketStatus.Todo, r.Ticket!.Status);
        Assert.Matches(@"^IPOP-ICT-\d{4}-000001$", r.Ticket.ExternalRef);
        Assert.Equal("screen.png", Assert.Single(r.Ticket.Attachments).FileName);
        Assert.Equal(1234, r.Ticket.Attachments[0].Size);
        var row = await x.T.Verify().Tickets.SingleAsync();
        Assert.Null(row.PendingPayload);
        Assert.NotNull(row.IdeskTicketId);
        Assert.Contains("contentBase64", Assert.Single(x.Idesk.Posted));
        Assert.Empty(x.Gw.Sent);   // the user saw the sheet; no "sent" notification
        Assert.Contains(x.Rt.Sent, s => s is ("00000024", TicketEvents.Changed));
    }

    [Fact]
    public async Task Create_validation_error_is_400_and_stores_nothing()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");

        Assert.Equal("title", (await x.Svc.CreateAsync(u.Id, Req(title: "Hi"))).Field);
        Assert.Equal("category", (await x.Svc.CreateAsync(u.Id, Req(category: "ORACLE_REQUEST"))).Field);
        Assert.Equal("category", (await x.Svc.CreateAsync(u.Id, Req(category: null))).Field);
        Assert.Equal("criticalReason", (await x.Svc.CreateAsync(u.Id, Req(priority: "CRITICAL"))).Field);
        Assert.Equal("attachments", (await x.Svc.CreateAsync(u.Id, Req(attachments: [new("run.exe", "AAAA")]))).Field);
        Assert.Equal("attachments", (await x.Svc.CreateAsync(u.Id, Req(attachments:
            [new("big.pdf", Convert.ToBase64String(new byte[TicketAttachmentRules.MaxBytes + 1]))]))).Field);
        Assert.Equal("attachments", (await x.Svc.CreateAsync(u.Id, Req(attachments:
            Enumerable.Range(0, 6).Select(i => new NewTicketAttachment($"f{i}.txt", "QQ==")).ToList()))).Field);

        Assert.Empty(x.Idesk.Posted);
        Assert.Equal(0, await x.T.Verify().Tickets.CountAsync());
    }

    [Fact]
    public async Task Default_category_and_hardware_rules()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");
        var oracle = await x.Svc.CreateAsync(u.Id, Req(module: "oracle-k2", category: null));
        Assert.Equal(TicketCreateKind.Created, oracle.Kind);
        Assert.Equal("ORACLE_REQUEST", oracle.Ticket!.Category);

        var today = new DateOnly(2026, 10, 5);   // a Monday
        Assert.Equal("hardwareType", TicketService.Validate(Req(category: "HARDWARE_INSTALLATION"), x.Idesk.Modules, today).Error?.Field);
        Assert.Equal("scheduledDate", TicketService.Validate(Req(category: "HARDWARE_INSTALLATION", hardwareType: "Printer",
            date: "2026-10-02", time: "09:00"), x.Idesk.Modules, today).Error?.Field);   // past
        Assert.Equal("scheduledDate", TicketService.Validate(Req(category: "HARDWARE_INSTALLATION", hardwareType: "Printer",
            date: "2026-10-10", time: "09:00"), x.Idesk.Modules, today).Error?.Field);   // Saturday
        Assert.Equal("scheduledTime", TicketService.Validate(Req(category: "HARDWARE_INSTALLATION", hardwareType: "Printer",
            date: "2026-10-06", time: "12:00"), x.Idesk.Modules, today).Error?.Field);
        Assert.Null(TicketService.Validate(Req(category: "HARDWARE_INSTALLATION", hardwareType: "Printer",
            date: "2026-10-06", time: "14:00"), x.Idesk.Modules, today).Error);
    }

    [Fact]
    public async Task Create_rejected_by_idesk_is_400_and_stores_nothing()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");
        x.Idesk.CreateResults.Enqueue(new(IdeskOutcome.Rejected, null, "category does not belong to module"));
        var r = await x.Svc.CreateAsync(u.Id, Req());
        Assert.Equal(TicketCreateKind.Rejected, r.Kind);
        Assert.Equal("category does not belong to module", r.Message);
        Assert.Equal(0, await x.T.Verify().Tickets.CountAsync());
    }

    [Fact]
    public async Task Create_422_is_not_registered_and_remembered_until_a_sync_succeeds()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");
        x.Idesk.CreateResults.Enqueue(new(IdeskOutcome.NotRegistered));
        Assert.Equal(TicketCreateKind.NotRegistered, (await x.Svc.CreateAsync(u.Id, Req())).Kind);
        Assert.Equal(0, await x.T.Verify().Tickets.CountAsync());
        Assert.True((await x.Svc.ListAsync(u.Id, false)).NotRegistered);

        Assert.False((await x.Svc.SyncAsync(u.Id, false)).NotRegistered);
    }

    [Fact]
    public async Task Create_timeout_is_queued_as_sending_and_retried_with_the_same_ref()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");
        x.Idesk.CreateResults.Enqueue(new(IdeskOutcome.Unreachable, null, "timeout"));

        var r = await x.Svc.CreateAsync(u.Id, Req());
        Assert.Equal(TicketCreateKind.Queued, r.Kind);
        Assert.Equal(TicketStatus.Sending, r.Ticket!.Status);
        var row = await x.T.Verify().Tickets.SingleAsync();
        Assert.NotNull(row.PendingPayload);
        Assert.Equal(1, row.SendAttempts);

        // Not due yet (1 min backoff), then due.
        Assert.Equal(0, await x.Svc.ProcessOutboxAsync(DateTime.UtcNow));
        await x.Svc.ProcessOutboxAsync(DateTime.UtcNow.AddMinutes(2));

        Assert.Equal(2, x.Idesk.Posted.Count);
        Assert.Equal(x.Idesk.Posted[0], x.Idesk.Posted[1]);   // same bytes, same externalRef
        var landed = await x.T.Verify().Tickets.SingleAsync();
        Assert.Equal(TicketStatus.Todo, landed.Status);
        Assert.Null(landed.PendingPayload);
        Assert.Equal("Ticket sent to ICT", Assert.Single(x.Gw.Sent).Title);
    }

    [Fact]
    public async Task External_refs_are_sequential_per_year()
    {
        var x = Setup(); using var _ = x.T;
        Assert.Equal("IPOP-ICT-2026-000001", await x.Svc.NextExternalRefAsync(2026));
        Assert.Equal("IPOP-ICT-2026-000002", await x.Svc.NextExternalRefAsync(2026));
        Assert.Equal("IPOP-ICT-2027-000001", await x.Svc.NextExternalRefAsync(2027));
    }

    // ── Outbox / retry / discard ─────────────────────────────────────────

    [Fact]
    public async Task Outbox_gives_up_after_24_hours()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");
        x.Idesk.CreateResults.Enqueue(new(IdeskOutcome.Unreachable));
        var id = (await x.Svc.CreateAsync(u.Id, Req())).Ticket!.Id;

        await x.Svc.ProcessOutboxAsync(DateTime.UtcNow.AddHours(25));

        var row = await x.T.Verify().Tickets.SingleAsync();
        Assert.Equal(TicketStatus.Failed, row.Status);
        Assert.Single(x.Idesk.Posted);   // no further attempt after the deadline
        var n = Assert.Single(x.Gw.Sent);
        Assert.Equal("Couldn't send your ticket", n.Title);
        Assert.Equal($"/tickets/{id}", n.Url);
        Assert.Equal(u.Id, n.To);

        // Try again now: lands; Discard only works while pending.
        var retried = await x.Svc.RetryAsync(u.Id, id);
        Assert.Equal(TicketRetryKind.Sent, retried.Kind);
        Assert.Equal(TicketDiscardKind.NotPending, await x.Svc.DiscardAsync(u.Id, id));
    }

    [Fact]
    public async Task Retry_unreachable_and_discard()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");
        x.Idesk.CreateResults.Enqueue(new(IdeskOutcome.Unreachable));
        var id = (await x.Svc.CreateAsync(u.Id, Req())).Ticket!.Id;
        x.Idesk.CreateResults.Enqueue(new(IdeskOutcome.Unreachable));
        Assert.Equal(TicketRetryKind.Unreachable, (await x.Svc.RetryAsync(u.Id, id)).Kind);
        Assert.Equal(TicketDiscardKind.Discarded, await x.Svc.DiscardAsync(u.Id, id));
        Assert.Equal(0, await x.T.Verify().Tickets.CountAsync());
    }

    // ── Webhook ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Webhook_for_an_idesk_filed_ticket_creates_the_mirror_by_nik()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("00000024");
        var remote = Remote(null, "00000024", TicketStatus.InProgress, assignee: "Rizky Aditya");

        Assert.Equal(TicketWebhookKind.Applied, await x.Svc.ApplyWebhookAsync(Webhook("e1", "ticket.assigned", remote)));

        var row = await x.T.Verify().Tickets.SingleAsync();
        Assert.Equal(u.Id, row.UserId);
        Assert.Equal("IDESK", row.Origin);
        Assert.Equal(remote.TicketId, row.IdeskTicketId);
        var n = Assert.Single(x.Gw.Sent);
        Assert.Equal("Rizky Aditya picked up your ticket", n.Title);
        Assert.Equal(remote.Title, n.Message);
        Assert.Equal($"/tickets/{row.Id}", n.Url);
        Assert.Contains(x.Rt.Sent, s => s is ("00000024", TicketEvents.Changed));
    }

    [Fact]
    public async Task Duplicate_webhook_event_is_ignored()
    {
        var x = Setup(); using var _ = x.T;
        x.T.AddUser("U1");
        var body = Webhook("same-id", "ticket.resolved", Remote(null, "U1", TicketStatus.Resolved), TicketStatus.InProgress);

        Assert.Equal(TicketWebhookKind.Applied, await x.Svc.ApplyWebhookAsync(body));
        Assert.Equal(TicketWebhookKind.Duplicate, await x.Svc.ApplyWebhookAsync(body));

        Assert.Equal("Ticket resolved — please confirm", Assert.Single(x.Gw.Sent).Title);
        Assert.Single(await x.T.Verify().TicketEvents.ToListAsync());
    }

    [Fact]
    public async Task Webhook_for_unknown_nik_is_a_no_op()
    {
        var x = Setup(); using var _ = x.T;
        Assert.Equal(TicketWebhookKind.UnknownRequester,
            await x.Svc.ApplyWebhookAsync(Webhook("e1", "ticket.created", Remote(null, "NOBODY"))));
        Assert.Equal(0, await x.T.Verify().Tickets.CountAsync());
        Assert.Empty(x.Gw.Sent);
    }

    [Fact]
    public async Task Webhook_matches_ipop_ticket_by_external_ref_and_stays_silent_on_created()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");
        var created = await x.Svc.CreateAsync(u.Id, Req());
        var row = await x.T.Verify().Tickets.SingleAsync();
        var remote = Remote(row.ExternalRef, "U1", id: row.IdeskTicketId, number: row.TicketNumber);

        Assert.Equal(TicketWebhookKind.Applied, await x.Svc.ApplyWebhookAsync(Webhook("c1", "ticket.created", remote)));
        Assert.Empty(x.Gw.Sent);

        var progress = remote with { Status = TicketStatus.InProgress, UpdatedAt = DateTime.UtcNow };
        await x.Svc.ApplyWebhookAsync(Webhook("s1", "ticket.status_changed", progress, TicketStatus.Todo));
        Assert.Equal("Ticket status: In progress", Assert.Single(x.Gw.Sent).Title);

        var closed = progress with { Status = TicketStatus.Closed, UpdatedAt = DateTime.UtcNow.AddSeconds(1) };
        await x.Svc.ApplyWebhookAsync(Webhook("x1", "ticket.closed", closed, TicketStatus.Resolved));
        Assert.Equal("Ticket closed", x.Gw.Sent[^1].Title);

        Assert.Equal(1, await x.T.Verify().Tickets.CountAsync());
        var detail = await x.Svc.GetAsync(u.Id, created.Ticket!.Id);
        Assert.Equal(TicketStatus.Closed, detail!.Status);
        Assert.Equal(["created", "status_changed", "closed"], detail.Events.Select(e => e.Kind).ToList());
    }

    // ── Sync / badge / isolation ─────────────────────────────────────────

    [Fact]
    public async Task Sync_upserts_and_soft_deletes_tickets_missing_from_idesk()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");
        var a = Remote(null, "U1"); var b = Remote(null, "U1", TicketStatus.Resolved);
        x.Idesk.ListResult = new(IdeskOutcome.Ok, [a, b]);
        var first = await x.Svc.SyncAsync(u.Id, false);
        Assert.Equal(2, first.Tickets.Count);
        Assert.NotNull(first.SyncedAt);
        Assert.Empty(x.Gw.Sent);   // sync never notifies

        x.Idesk.ListResult = new(IdeskOutcome.Ok, [a]);
        var second = await x.Svc.SyncAsync(u.Id, false);
        Assert.Equal(a.TicketId, (await x.T.Verify().Tickets.SingleAsync(t => t.Id == second.Tickets.Single().Id)).IdeskTicketId);
        Assert.NotNull((await x.T.Verify().Tickets.SingleAsync(t => t.IdeskTicketId == b.TicketId)).RemovedAt);

        x.Idesk.ListResult = new(IdeskOutcome.Unreachable);
        var offline = await x.Svc.SyncAsync(u.Id, false);
        Assert.True(offline.Offline);
        Assert.Single(offline.Tickets);
    }

    [Fact]
    public async Task Reconcile_updates_silently_and_404_removes()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");
        var a = Remote(null, "U1"); var b = Remote(null, "U1");
        x.Idesk.ListResult = new(IdeskOutcome.Ok, [a, b]);
        await x.Svc.SyncAsync(u.Id, false);
        await x.T.Db.Tickets.ExecuteUpdateAsync(s => s.SetProperty(t => t.RefreshedAt, (DateTime?)null));

        x.Idesk.ByNumber[a.TicketNumber!] = new(IdeskOutcome.Ok, a with { Status = TicketStatus.InProgress, UpdatedAt = DateTime.UtcNow });
        Assert.Equal(2, await x.Svc.ReconcileAsync());

        var v = x.T.Verify();
        Assert.Equal(TicketStatus.InProgress, (await v.Tickets.SingleAsync(t => t.IdeskTicketId == a.TicketId)).Status);
        Assert.NotNull((await v.Tickets.SingleAsync(t => t.IdeskTicketId == b.TicketId)).RemovedAt);
        Assert.Empty(x.Gw.Sent);
    }

    [Fact]
    public async Task Badge_counts_only_the_users_resolved_tickets()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1"); var other = x.T.AddUser("U2");
        await x.Svc.ApplyWebhookAsync(Webhook("1", "ticket.resolved", Remote(null, "U1", TicketStatus.Resolved)));
        await x.Svc.ApplyWebhookAsync(Webhook("2", "ticket.resolved", Remote(null, "U1", TicketStatus.Resolved)));
        await x.Svc.ApplyWebhookAsync(Webhook("3", "ticket.status_changed", Remote(null, "U1", TicketStatus.InProgress)));
        await x.Svc.ApplyWebhookAsync(Webhook("4", "ticket.resolved", Remote(null, "U2", TicketStatus.Resolved)));
        await x.T.Db.Tickets.Where(t => t.UserId == u.Id && t.Status == TicketStatus.Resolved).Take(1)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RemovedAt, DateTime.UtcNow));

        var badge = new TicketsBadgeProvider(x.T.Verify());
        Assert.Equal("tickets", badge.Key);
        Assert.Equal(1, await badge.CountAsync(u.Id, default));
        Assert.Equal(1, await badge.CountAsync(other.Id, default));
    }

    [Fact]
    public async Task Users_never_see_another_users_ticket()
    {
        var x = Setup(); using var _ = x.T;
        var owner = x.T.AddUser("U1"); var admin = x.T.AddAdmin("A1");
        var id = (await x.Svc.CreateAsync(owner.Id, Req())).Ticket!.Id;

        Assert.Empty((await x.Svc.ListAsync(admin.Id, false)).Tickets);
        Assert.Null(await x.Svc.GetAsync(admin.Id, id));
        Assert.Equal(TicketRetryKind.NotFound, (await x.Svc.RetryAsync(admin.Id, id)).Kind);
        Assert.Equal(TicketDiscardKind.NotFound, await x.Svc.DiscardAsync(admin.Id, id));
        Assert.Single((await x.Svc.ListAsync(owner.Id, false)).Tickets);
    }

    [Fact]
    public async Task Open_filter_hides_closed_tickets()
    {
        var x = Setup(); using var _ = x.T;
        var u = x.T.AddUser("U1");
        x.Idesk.ListResult = new(IdeskOutcome.Ok, [Remote(null, "U1"), Remote(null, "U1", TicketStatus.Closed)]);
        await x.Svc.SyncAsync(u.Id, false);
        Assert.Single((await x.Svc.ListAsync(u.Id, true)).Tickets);
        Assert.Equal(2, (await x.Svc.ListAsync(u.Id, false)).Tickets.Count);
    }
}
