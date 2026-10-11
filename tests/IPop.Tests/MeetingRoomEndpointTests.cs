using IPop.Api.Endpoints;
using IPop.Application.MeetingRooms;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IPop.Tests;

public class MeetingRoomEndpointTests
{
    private static int Status(IResult r) => r is IStatusCodeHttpResult s ? s.StatusCode ?? 200 : 200;

    [Theory]
    [InlineData("unknown_room", 404)]
    [InlineData("room_full", 409)]
    [InlineData("media_unavailable", 503)]
    public void Join_errors_map_to_status(string code, int status) =>
        Assert.Equal(status, Status(MeetingRoomEndpoints.JoinResponse(new(null, code, ["kalosi"]))));

    [Theory]
    [InlineData("unknown_room", 404)]
    [InlineData("not_found", 404)]
    [InlineData("not_in_room", 403)]
    [InlineData("already_recording", 409)]
    [InlineData("egress_unavailable", 503)]
    public void Recording_errors_map_to_status(string code, int status) =>
        Assert.Equal(status, Status(MeetingRoomEndpoints.RecordingResponse(new(null, code))));

    [Fact]
    public void Join_success_is_ok() =>
        Assert.Equal(200, Status(MeetingRoomEndpoints.JoinResponse(new(new("kalosi", "Kalosi", "hall-kalosi", "wss://x", "t", true, "id"), null, []))));
}
