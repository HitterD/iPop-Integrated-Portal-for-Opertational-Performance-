using IPop.Application.Chat;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// Unsent messages read back from localStorage after a reload: what is resent, what waits.
public class ChatOutboxTests
{
    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });
    }

    private static ChatState NewChat(Bunit.BunitContext ctx)
    {
        var http = new HttpClient(new NotFound()) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth,
            new DirectoryEmployeeDto("A", "Me", null, null, null, null, null, null, "a@x", null, null, "on", UserId: Guid.NewGuid()));
        return new ChatState(auth, http);
    }

    private static ChatState.StoredOutgoing Stored(SendState state, DateTime createdAt, Guid? key = null, Guid? room = null) =>
        new(Guid.NewGuid(), key ?? Guid.NewGuid(), room ?? Guid.NewGuid(), "hello", null, "text", null, null, false,
            state, state == SendState.Failed ? "Not allowed" : null, createdAt);

    [Fact]
    public void Restored_entries_are_resent_unless_the_server_refused_them()
    {
        using var ctx = new Bunit.BunitContext();
        var chat = NewChat(ctx);
        var now = DateTime.UtcNow;
        var pending = Stored(SendState.Pending, now.AddMinutes(-3));
        var queued = Stored(SendState.Queued, now.AddMinutes(-2));
        var failed = Stored(SendState.Failed, now.AddMinutes(-1));

        chat.RestoreOutbox([pending, queued, failed], now);

        var back = chat.SnapshotOutbox();
        Assert.Equal([pending.Key, queued.Key, failed.Key], back.Select(s => s.Key));
        Assert.Equal([SendState.Unknown, SendState.Unknown, SendState.Failed], back.Select(s => s.State));
        Assert.Equal("Not allowed", back[2].Error);
        Assert.Equal(pending.CreatedAt, back[0].CreatedAt);
    }

    [Fact]
    public void Entries_past_seven_days_and_duplicates_are_dropped()
    {
        using var ctx = new Bunit.BunitContext();
        var chat = NewChat(ctx);
        var now = DateTime.UtcNow;
        var fresh = Stored(SendState.Queued, now.AddDays(-6));
        var expired = Stored(SendState.Queued, now.AddDays(-8));
        var dupe = fresh with { TempId = Guid.NewGuid() };

        chat.RestoreOutbox([fresh, expired, dupe], now);
        // A second read-back (another tab wrote the same entries) adds nothing.
        chat.RestoreOutbox([fresh], now);

        Assert.Equal([fresh.Key], chat.SnapshotOutbox().Select(s => s.Key));
    }

    [Fact]
    public void Stored_entries_round_trip_through_json()
    {
        var s = Stored(SendState.Unknown, DateTime.UtcNow) with { AttachmentIds = [Guid.NewGuid()], ReplyToId = Guid.NewGuid() };
        var json = System.Text.Json.JsonSerializer.Serialize(new List<ChatState.StoredOutgoing> { s });
        var back = System.Text.Json.JsonSerializer.Deserialize<List<ChatState.StoredOutgoing>>(json)!;

        Assert.Equal(s.Key, back[0].Key);
        Assert.Equal(s.ReplyToId, back[0].ReplyToId);
        Assert.Equal(s.AttachmentIds, back[0].AttachmentIds);
        Assert.Equal(s.State, back[0].State);
    }
}
