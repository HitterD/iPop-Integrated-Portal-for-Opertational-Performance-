using System.Net;
using Bunit;
using IPop.Application.Calls;
using IPop.Web.Components.Calls;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// The in-room meeting view (FaceTime-style) and its minimized pill, driven by MeetingRoomState.
[Collection("AvatarPhotos")]
public class MeetingRoomOverlayTests
{
    [Fact]
    public void Nothing_renders_when_not_in_a_room()
    {
        using var ctx = Context(out _);
        Assert.Empty(ctx.Render<MeetingRoomOverlay>().FindAll(".mr-meet, .mr-pill"));
    }

    [Fact]
    public void In_a_room_shows_controls_and_recording_menu_with_both_choices()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("arabika", "Arabika", "hall-arabika", "wss://x", "t", true, "me"));
        var cut = ctx.Render<MeetingRoomOverlay>();
        Assert.Contains("Arabika", cut.Find(".mr-top").TextContent);
        cut.Find("button.mr-rec").Click();
        var menu = cut.Find(".mr-menu").TextContent;
        Assert.Contains("Record to this PC", menu);
        Assert.Contains("Record to server", menu);
        Assert.Contains("Everyone here will see that you're recording.", menu);
    }

    [Fact]
    public void Server_recording_shows_the_banner_with_who_started_it_and_greys_the_server_option()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("arabika", "Arabika", "hall-arabika", "wss://x", "t", true, "me"));
        state.OnRoomMetadata("""{"recording":{"id":"7f1c0a4e-0000-4000-8000-000000000001","by":"Budi","startedAt":"2026-10-11T03:00:00Z"}}""");
        var cut = ctx.Render<MeetingRoomOverlay>();
        Assert.Contains("started by Budi", cut.Find(".mr-recbar").TextContent);
    }

    [Fact]
    public void Clearing_the_room_metadata_removes_the_server_recording_banner()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("arabika", "Arabika", "hall-arabika", "wss://x", "t", true, "me"));
        state.OnRoomMetadata("""{"recording":{"id":"7f1c0a4e-0000-4000-8000-000000000001","by":"Budi","startedAt":"2026-10-11T03:00:00Z"}}""");
        var cut = ctx.Render<MeetingRoomOverlay>();
        state.OnRoomMetadata("{}");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mr-recbar")));
        Assert.Null(state.RecordingServer);
    }

    [Fact]
    public void Video_not_allowed_disables_the_camera_button()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("kalosi", "Kalosi", "hall-kalosi", "wss://x", "t", false, "me"));
        Assert.True(ctx.Render<MeetingRoomOverlay>().Find("button.mr-cam").HasAttribute("disabled"));
    }

    [Fact]
    public void Video_off_notice_shows_the_island_and_disables_the_camera()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("arabika", "Arabika", "hall-arabika", "wss://x", "t", true, "me"));
        var cut = ctx.Render<MeetingRoomOverlay>();
        Assert.False(cut.Find("button.mr-cam").HasAttribute("disabled"));
        state.OnRoomNotice("video_off", null);
        cut.WaitForAssertion(() => Assert.Contains("Video turned off for Arabika", cut.Find(".mr-island").TextContent));
        Assert.True(cut.Find("button.mr-cam").HasAttribute("disabled"));
    }

    [Fact]
    public void People_from_the_browser_fill_the_subtitle_and_the_people_sheet()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("arabika", "Arabika", "hall-arabika", "wss://x", "t", true, "me"));
        state.OnRoomPeople([
            new("me", "Bagastyo", false, true, false, false, false, true),
            new("u2", "Budi Darmawan", true, true, false, true, false, false),
        ]);
        var cut = ctx.Render<MeetingRoomOverlay>();
        Assert.Contains("2 people", cut.Find(".mr-top").TextContent);
        Assert.True(state.MicOn);
        cut.Find("button.mr-people-btn").Click();
        var sheet = cut.Find(".mr-people").TextContent;
        Assert.Contains("2 of 20", sheet);
        Assert.Contains("Budi Darmawan", sheet);
        Assert.Contains("Sharing screen", sheet);
    }

    [Fact]
    public void Minimize_shows_the_pill_and_keeps_the_media_containers()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("kalosi", "Kalosi", "hall-kalosi", "wss://x", "t", true, "me"));
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find("button.mr-minimize").Click();
        Assert.NotNull(cut.Find(".mr-pill"));
        Assert.NotNull(cut.Find("#mr-strip"));               // still in the DOM so video keeps playing
        Assert.NotNull(cut.Find("#mr-stage"));
        Assert.Contains("mr-hidden", cut.Find(".mr-meet").ClassName);
        cut.Find(".mr-pill").Click();
        Assert.DoesNotContain("mr-hidden", cut.Find(".mr-meet").ClassName);
    }

    [Fact]
    public async Task Join_is_refused_while_a_call_is_live()
    {
        using var ctx = Context(out var state, out var calls);
        typeof(CallState).GetProperty(nameof(CallState.Current))!.SetValue(calls, new CallRoomDto(
            Guid.NewGuid(), CallKind.User, CallStatus.Active, null, Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow, null, null,
            [new CallParticipantDto(Guid.NewGuid(), "200", "Budi Santoso", CallParticipantStatus.Accepted, false, false, null)]));
        Assert.True(calls.HasLiveCall);
        var (error, _) = await state.JoinAsync("arabika");
        Assert.Equal("in_call", error);
        Assert.Null(state.Room);
    }

    private static BunitContext Context(out MeetingRoomState state) => Context(out state, out _);

    private static BunitContext Context(out MeetingRoomState state, out CallState calls)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new NotFound()) { BaseAddress = new Uri("http://localhost/") };
        var js = ctx.JSInterop.JSRuntime;
        var auth = new AuthState(http, js);
        calls = new CallState(auth, http, js);
        state = new MeetingRoomState(auth, js, calls);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(calls);
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
