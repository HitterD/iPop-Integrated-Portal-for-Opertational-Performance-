using System.Net;
using System.Text;
using System.Text.Json;
using IPop.Infrastructure.Calls;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class LiveKitRoomAdminTests
{
    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Calls.Add((r.RequestUri!.AbsolutePath, body));
            return respond(r, body);
        }
    }

    private static (LiveKitRoomAdmin Admin, Stub Http) Make(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Calls:LiveKit:ApiKey"] = "k", ["Calls:LiveKit:ApiSecret"] = "s-123456789012345678901234567890",
            ["Calls:LiveKit:Url"] = "ws://lk.test:7880",
        }).Build();
        var stub = new Stub(respond);
        return (new LiveKitRoomAdmin(new HttpClient(stub), new LiveKitTokenService(cfg)), stub);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task List_parses_participants_tracks_and_attributes()
    {
        var (admin, http) = Make((_, _) => Json("""
            {"participants":[{"identity":"u1","name":"Sari","attributes":{"rec":"pc"},
              "tracks":[{"sid":"T1","source":"CAMERA","muted":false},{"sid":"T2","source":"SCREEN_SHARE"}]}]}
            """));
        var p = Assert.Single(await admin.ListAsync("hall-kalosi", default));
        Assert.Equal(("u1", "Sari", "pc"), (p.Identity, p.Name, p.Attributes["rec"]));
        Assert.True(p.Tracks[0].IsSource("CAMERA"));
        Assert.True(p.Tracks[1].IsSource("SCREEN_SHARE"));
        Assert.Equal("/twirp/livekit.RoomService/ListParticipants", http.Calls[0].Path);
    }

    [Fact]
    public async Task Missing_room_lists_nobody()
    {
        var (admin, _) = Make((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Empty(await admin.ListAsync("hall-x", default));
    }

    [Fact]
    public async Task Set_sources_updates_permission_and_mutes_camera_tracks_no_longer_allowed()
    {
        var (admin, http) = Make((r, _) => r.RequestUri!.AbsolutePath.EndsWith("ListParticipants")
            ? Json("""{"participants":[{"identity":"u1","name":"A","tracks":[{"sid":"C1","source":"CAMERA","muted":false},{"sid":"M1","source":"MICROPHONE","muted":false}]}]}""")
            : Json("{}"));
        Assert.True(await admin.SetSourcesAsync("hall-kalosi", "u1", ["microphone", "screen_share"], default));

        var update = http.Calls.Single(c => c.Path.EndsWith("UpdateParticipant"));
        Assert.Contains("\"can_publish_sources\":[\"MICROPHONE\",\"SCREEN_SHARE\"]", update.Body);
        var mute = http.Calls.Single(c => c.Path.EndsWith("MutePublishedTrack"));
        Assert.Contains("\"track_sid\":\"C1\"", mute.Body);
    }

    [Fact]
    public async Task Empty_sources_disables_publishing()
    {
        var (admin, http) = Make((r, _) => r.RequestUri!.AbsolutePath.EndsWith("ListParticipants")
            ? Json("""{"participants":[{"identity":"u1"}]}""")
            : Json("{}"));
        Assert.True(await admin.SetSourcesAsync("hall-kalosi", "u1", [], default));

        using var body = JsonDocument.Parse(http.Calls.Single(c => c.Path.EndsWith("UpdateParticipant")).Body);
        var permission = body.RootElement.GetProperty("permission");
        Assert.False(permission.GetProperty("can_publish").GetBoolean());
        Assert.Empty(permission.GetProperty("can_publish_sources").EnumerateArray());
    }

    [Fact]
    public async Task Set_sources_returns_false_when_participant_leaves_before_update()
    {
        var (admin, http) = Make((r, _) => r.RequestUri!.AbsolutePath.EndsWith("ListParticipants")
            ? Json("""{"participants":[{"identity":"u1","tracks":[{"sid":"C1","source":"CAMERA"}]}]}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.False(await admin.SetSourcesAsync("hall-kalosi", "u1", ["microphone"], default));
        Assert.DoesNotContain(http.Calls, c => c.Path.EndsWith("MutePublishedTrack"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Muting_returns_false_when_track_mutation_returns_not_found(bool setSources)
    {
        var (admin, _) = Make((r, _) => r.RequestUri!.AbsolutePath.EndsWith("ListParticipants")
            ? Json("""{"participants":[{"identity":"u1","tracks":[{"sid":"M1","source":"MICROPHONE","muted":false}]}]}""")
            : r.RequestUri.AbsolutePath.EndsWith("MutePublishedTrack")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json("{}"));

        var result = setSources
            ? await admin.SetSourcesAsync("hall-kalosi", "u1", ["camera"], default)
            : await admin.MuteAsync("hall-kalosi", "u1", default);
        Assert.False(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Room_mutations_throw_when_room_returns_not_found(bool updateMetadata)
    {
        var (admin, _) = Make((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => updateMetadata
            ? admin.UpdateRoomMetadataAsync("hall-kalosi", "{}", default)
            : admin.CreateRoomAsync("hall-kalosi", 21, default));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task Create_room_sends_max_participants()
    {
        var (admin, http) = Make((_, _) => Json("{}"));
        await admin.CreateRoomAsync("hall-toraja", 21, default);
        Assert.Contains("\"max_participants\":21", http.Calls.Single().Body);
    }
}
