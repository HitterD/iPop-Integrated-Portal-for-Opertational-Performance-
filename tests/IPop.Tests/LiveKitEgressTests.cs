using System.Net;
using System.Text;
using System.Text.Json;
using IPop.Infrastructure.Calls;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class LiveKitEgressTests
{
    private sealed class Handler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri;
        public string? Body;
        public string? Authorization;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(ct);
            Authorization = request.Headers.Authorization?.ToString();
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    [Theory]
    [InlineData(null, "https://lk.test")]
    [InlineData("http://127.0.0.1:7880", "http://127.0.0.1:7880")]
    public async Task Twirp_uses_server_override_or_falls_back_to_client_url(string? serverUrl, string expectedBase)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Calls:LiveKit:Url"] = "wss://lk.test",
            ["Calls:LiveKit:ServerUrl"] = serverUrl,
            ["Calls:LiveKit:ApiKey"] = "test-key",
            ["Calls:LiveKit:ApiSecret"] = "test-secret",
        }).Build();
        var handler = new Handler("{}");
        var client = new LiveKitEgress(new HttpClient(handler), new LiveKitTokenService(config));
        await client.StopAsync("EG_test", default);
        Assert.Equal(expectedBase + "/twirp/livekit.Egress/StopEgress", handler.Uri!.ToString());
    }

    [Fact]
    public async Task Start_posts_room_composite_mp4_to_the_https_twirp_endpoint()
    {
        var handler = new Handler("{\"egress_id\":\"EG_test\"}");
        var client = new LiveKitEgress(new HttpClient(handler), new MeetingRoomServiceTests.FakeTokens());
        Assert.True(client.IsConfigured);
        Assert.Equal("EG_test", await client.StartRoomCompositeAsync("hall-kalosi", "/out/kalosi/test.mp4", default));
        Assert.Equal("https://lk.test/twirp/livekit.Egress/StartRoomCompositeEgress", handler.Uri!.ToString());
        Assert.Equal("Bearer admin", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("hall-kalosi", root.GetProperty("room_name").GetString());
        Assert.Equal("speaker", root.GetProperty("layout").GetString());
        Assert.Equal("H264_720P_30", root.GetProperty("preset").GetString());
        Assert.Equal("MP4", root.GetProperty("file_outputs")[0].GetProperty("file_type").GetString());
        Assert.Equal("/out/kalosi/test.mp4", root.GetProperty("file_outputs")[0].GetProperty("filepath").GetString());
    }

    [Theory]
    [InlineData("0", "EGRESS_STARTING")]
    [InlineData("1", "EGRESS_ACTIVE")]
    [InlineData("2", "EGRESS_ENDING")]
    [InlineData("3", "EGRESS_COMPLETE")]
    [InlineData("4", "EGRESS_FAILED")]
    [InlineData("5", "EGRESS_ABORTED")]
    [InlineData("6", "EGRESS_LIMIT_REACHED")]
    [InlineData("\"EGRESS_COMPLETE\"", "EGRESS_COMPLETE")]
    public async Task Get_maps_numeric_and_named_statuses_and_protobuf_int64_strings(string status, string expected)
    {
        var handler = new Handler("{\"items\":[{\"status\":" + status + ",\"file_results\":[{\"filename\":\"/out/test.mp4\",\"size\":\"123\",\"duration\":\"9000000000\"}],\"error\":\"failure\"}]}");
        var client = new LiveKitEgress(new HttpClient(handler), new MeetingRoomServiceTests.FakeTokens());
        Assert.Equal(new LiveKitEgressInfo(expected, "/out/test.mp4", 123, 9000000000, "failure"), await client.GetAsync("EG_test", default));
        Assert.EndsWith("/ListEgress", handler.Uri!.ToString());
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("EG_test", body.RootElement.GetProperty("egress_id").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"items\":[]}")]
    public async Task Get_returns_null_when_egress_is_missing(string response)
    {
        var client = new LiveKitEgress(new HttpClient(new Handler(response)), new MeetingRoomServiceTests.FakeTokens());
        Assert.Null(await client.GetAsync("missing", default));
    }

    [Fact]
    public async Task Stop_posts_the_egress_id_and_http_errors_propagate()
    {
        var handler = new Handler("{}");
        var client = new LiveKitEgress(new HttpClient(handler), new MeetingRoomServiceTests.FakeTokens());
        await client.StopAsync("EG_test", default);
        Assert.EndsWith("/StopEgress", handler.Uri!.ToString());
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("EG_test", body.RootElement.GetProperty("egress_id").GetString());
        var unavailable = new LiveKitEgress(new HttpClient(new Handler("{}", HttpStatusCode.ServiceUnavailable)), new MeetingRoomServiceTests.FakeTokens());
        await Assert.ThrowsAsync<HttpRequestException>(() => unavailable.StopAsync("EG_test", default));
    }
}
