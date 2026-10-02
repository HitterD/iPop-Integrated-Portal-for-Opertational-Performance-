using System.Net;
using System.Net.Http.Json;
using IPop.Application.Chat;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// Outgoing messages are settled by (room, idempotency key) only: a rejection marks exactly the
/// message it answers, acks/echoes arriving in any order (or twice) never duplicate or lose a
/// bubble, a reload keeps unconfirmed bubbles, and retry reuses the original key.
public class ChatSendReliabilityTests
{
    private sealed class Api(Guid room, Func<IReadOnlyList<MessageDto>> page) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.PathAndQuery;
            HttpResponseMessage res = url.StartsWith($"/api/chat/rooms/{room}/messages", StringComparison.Ordinal)
                ? new(HttpStatusCode.OK) { Content = JsonContent.Create(new PagedResult<MessageDto>(page(), page().Count, 1, 50)) }
                : url == $"/api/chat/rooms/{room}"
                    ? new(HttpStatusCode.OK) { Content = JsonContent.Create(new ChatRoomDto(room, "dm", "Room", null, 2, 0, [], "member", [])) }
                    : new(HttpStatusCode.NotFound);
            return Task.FromResult(res);
        }
    }

    private sealed class Rig : IDisposable
    {
        public Bunit.BunitContext Ctx { get; } = new();
        public Guid Room { get; } = Guid.NewGuid();
        public List<MessageDto> ServerPage { get; } = [];
        public ChatState Chat { get; }
        public List<SendOutcome> Settled { get; } = [];
        public Rig()
        {
            Ctx.JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
            var http = new HttpClient(new Api(Room, () => ServerPage.ToList())) { BaseAddress = new Uri("http://localhost/") };
            Chat = new ChatState(new AuthState(http, Ctx.JSInterop.JSRuntime), http);
            Chat.SendSettled += Settled.Add;
        }
        public void Dispose() => Ctx.Dispose();
    }

    private static MessageDto Real(Guid room, string content, Guid key) => new(Guid.NewGuid(), room, Guid.NewGuid(), "Me", "",
        "text", content, DateTime.UtcNow, null, false, null, null, null, null, 0, null, [], [], [], false, key);

    private static async Task<Rig> OpenAsync()
    {
        var rig = new Rig();
        await rig.Chat.OpenRoomAsync(rig.Room);
        return rig;
    }

    [Fact]
    public async Task Rejection_marks_only_the_message_with_that_key_not_the_oldest_pending()
    {
        using var rig = await OpenAsync();
        var a = (await rig.Chat.SendMessageAsync("first"))!;
        var b = (await rig.Chat.SendMessageAsync("second"))!;
        var c = (await rig.Chat.SendMessageAsync("third"))!;

        rig.Chat.ApplySendResult(rig.Room, b.IdempotencyKey,
            new SendMessageResultDto(false, rig.Room, b.IdempotencyKey, Error: "blocked"));

        Assert.True(rig.Chat.IsFailed(b.TempId));
        Assert.False(rig.Chat.IsFailed(a.TempId));
        Assert.False(rig.Chat.IsFailed(c.TempId));
        Assert.Equal(ChatState.SendErrorText("blocked"), rig.Chat.SendFailureText(b.TempId));
        // Content stays on the failed bubble.
        Assert.Equal("second", rig.Chat.ActiveMessages.Single(m => m.Id == b.TempId).Content);
        var settled = Assert.Single(rig.Settled);
        Assert.Equal((b.IdempotencyKey, SendState.Failed, "blocked"), (settled.IdempotencyKey, settled.State, settled.Error));
    }

    [Fact]
    public async Task Result_for_the_same_key_in_another_room_is_ignored()
    {
        using var rig = await OpenAsync();
        var a = (await rig.Chat.SendMessageAsync("hi"))!;

        rig.Chat.ApplySendResult(Guid.NewGuid(), a.IdempotencyKey,
            new SendMessageResultDto(false, Guid.Empty, a.IdempotencyKey, Error: "forbidden"));

        Assert.False(rig.Chat.IsFailed(a.TempId));
        Assert.Empty(rig.Settled);
    }

    [Fact]
    public async Task Null_result_is_an_unknown_outcome_with_a_safe_retry_message()
    {
        using var rig = await OpenAsync();
        var a = (await rig.Chat.SendMessageAsync("hi"))!;

        rig.Chat.ApplySendResult(rig.Room, a.IdempotencyKey, null);

        Assert.True(rig.Chat.IsFailed(a.TempId));
        Assert.Equal(ChatState.UnknownOutcomeText, rig.Chat.SendFailureText(a.TempId));
        Assert.Equal(SendState.Unknown, Assert.Single(rig.Settled).State);
    }

    [Theory]
    [InlineData(true)]  // ack, then room echo
    [InlineData(false)] // room echo, then ack
    public async Task Ack_and_echo_in_either_order_leave_exactly_one_real_row(bool ackFirst)
    {
        using var rig = await OpenAsync();
        var a = (await rig.Chat.SendMessageAsync("hello"))!;
        var real = Real(rig.Room, "hello", a.IdempotencyKey);
        var ack = new MessageAckDto(real.Id, real.CreatedAt, a.IdempotencyKey);

        if (ackFirst) { rig.Chat.OnMessageAck(ack); rig.Chat.ReconcileEcho(real); }
        else { rig.Chat.ReconcileEcho(real); rig.Chat.OnMessageAck(ack); }
        rig.Chat.ReconcileEcho(real); // duplicate hub delivery
        rig.Chat.OnMessageAck(ack);   // duplicate ack
        rig.Chat.ApplySendResult(rig.Room, a.IdempotencyKey,
            new SendMessageResultDto(true, rig.Room, a.IdempotencyKey, real.Id)); // late invoke result

        var only = Assert.Single(rig.Chat.ActiveMessages);
        Assert.Equal(real.Id, only.Id);
        Assert.False(rig.Chat.IsPending(a.TempId) || rig.Chat.IsFailed(a.TempId));
        Assert.Equal(SendState.Confirmed, Assert.Single(rig.Settled).State); // settled once
    }

    [Fact]
    public async Task Echo_with_same_content_but_another_key_does_not_consume_my_bubble()
    {
        using var rig = await OpenAsync();
        var a = (await rig.Chat.SendMessageAsync("ok"))!;

        Assert.False(rig.Chat.ReconcileEcho(Real(rig.Room, "ok", Guid.NewGuid())));
        Assert.True(rig.Chat.IsPending(a.TempId));
    }

    [Fact]
    public async Task Success_after_an_unknown_timeout_confirms_the_message()
    {
        using var rig = await OpenAsync();
        var a = (await rig.Chat.SendMessageAsync("hi"))!;
        rig.Chat.ApplySendResult(rig.Room, a.IdempotencyKey, null);

        var real = Real(rig.Room, "hi", a.IdempotencyKey);
        rig.Chat.OnMessageAck(new MessageAckDto(real.Id, real.CreatedAt, a.IdempotencyKey));

        Assert.False(rig.Chat.IsFailed(a.TempId));
        Assert.Equal([SendState.Unknown, SendState.Confirmed], rig.Settled.Select(s => s.State));
    }

    [Fact]
    public async Task Page_one_reload_keeps_unconfirmed_bubbles_and_confirms_ones_the_server_has()
    {
        using var rig = await OpenAsync();
        var sent = (await rig.Chat.SendMessageAsync("made it"))!;
        var failed = (await rig.Chat.SendMessageAsync("refused"))!;
        var queued = (await rig.Chat.SendMessageAsync("still queued"))!;
        rig.Chat.ApplySendResult(rig.Room, failed.IdempotencyKey,
            new SendMessageResultDto(false, rig.Room, failed.IdempotencyKey, Error: "rate_limited"));
        var serverRow = Real(rig.Room, "made it", sent.IdempotencyKey);
        rig.ServerPage.Add(serverRow);

        var merged = rig.Chat.ReconcileOptimisticHistory(rig.Room, rig.ServerPage);

        Assert.Equal([serverRow.Id, failed.TempId, queued.TempId], merged.Select(m => m.Id));
        Assert.True(rig.Chat.IsFailed(failed.TempId));
        Assert.True(rig.Chat.IsPending(queued.TempId));
        Assert.False(rig.Chat.IsPending(sent.TempId));
        // Another room's history never carries this room's bubbles.
        Assert.Empty(rig.Chat.ReconcileOptimisticHistory(Guid.NewGuid(), []));
    }

    [Fact]
    public async Task Retry_reuses_the_original_idempotency_key()
    {
        using var rig = await OpenAsync();
        var a = (await rig.Chat.SendMessageAsync("again"))!;
        rig.Chat.ApplySendResult(rig.Room, a.IdempotencyKey,
            new SendMessageResultDto(false, rig.Room, a.IdempotencyKey, Error: "rate_limited"));

        await rig.Chat.RetryAsync(a.TempId); // offline in tests: goes back to the queue

        Assert.True(rig.Chat.IsPending(a.TempId));
        var bubble = rig.Chat.ActiveMessages.Single(m => m.Id == a.TempId);
        Assert.Equal(a.IdempotencyKey, bubble.IdempotencyKey);
        // A later answer under that same key still settles it.
        rig.Chat.ApplySendResult(rig.Room, a.IdempotencyKey,
            new SendMessageResultDto(true, rig.Room, a.IdempotencyKey, Guid.NewGuid()));
        Assert.Equal(SendState.Confirmed, rig.Settled.Last().State);
    }

    [Fact]
    public async Task Discard_returns_content_and_attachments_for_the_composer()
    {
        using var rig = await OpenAsync();
        var att = new AttachmentDto(Guid.NewGuid(), "a.png", 10, "image/png", null);
        var a = (await rig.Chat.SendMessageAsync("caption", null, null, [att.Id], [att], localPreviewUrls: ["blob:x"]))!;
        rig.Chat.ApplySendResult(rig.Room, a.IdempotencyKey,
            new SendMessageResultDto(false, rig.Room, a.IdempotencyKey, Error: "message_not_found"));

        var back = rig.Chat.DiscardFailed(a.TempId)!;

        Assert.Equal("caption", back.Content);
        Assert.Equal([att.Id], back.AttachmentIds);
        Assert.Equal(["blob:x"], back.LocalPreviewUrls);
        Assert.Empty(rig.Chat.ActiveMessages);
    }

    [Fact]
    public async Task Over_long_message_is_refused_without_a_bubble()
    {
        using var rig = await OpenAsync();
        var r = await rig.Chat.SendMessageAsync(new string('x', ChatState.MaxMessageLength + 1));

        Assert.Equal(SendState.Failed, r!.State);
        Assert.Empty(rig.Chat.ActiveMessages);
    }
}
