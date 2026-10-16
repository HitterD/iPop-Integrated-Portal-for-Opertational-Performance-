using System.Net;
using Bunit;
using IPop.Application.Calls;
using IPop.Web.Components.Calls;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Wave 2 phone calls (mockup v2 C1 / C1b): on a phone the call window is a full screen and the
/// minimised call is a pill pinned under the status bar — neither is draggable (call-drag.js off).
[Collection("AvatarPhotos")]
public class PhoneCallsTests
{
    [Fact]
    public void Phone_call_is_a_full_screen_without_drag_and_minimises_to_a_pill()
    {
        using var ctx = Context(out var calls, out var phone);
        phone.OnPhoneChanged(true);
        SetCurrent(calls, ActiveCall());

        var cut = ctx.Render<GlobalCallOverlay>();

        var win = cut.Find(".callx-window.m-call-full");
        Assert.Empty(cut.FindAll(".callx-window .callx-resize"));
        Assert.Empty(ctx.JSInterop.Invocations["ipopCallDrag.attach"]);
        Assert.NotNull(win.QuerySelector("button[aria-label='Minimise call']"));

        cut.Find("button[aria-label='Minimise call']").Click();
        var pill = cut.Find(".callx-pill.m-call-pill");
        Assert.Contains("tap to return", pill.TextContent);
        Assert.NotNull(pill.QuerySelector("button[aria-label='Mute microphone']"));
        Assert.NotNull(pill.QuerySelector("button[aria-label='End call']"));
        Assert.Empty(ctx.JSInterop.Invocations["ipopCallDrag.attach"]);

        pill.QuerySelector(".callx-pill-open")!.Click();
        Assert.NotEmpty(cut.FindAll(".callx-window.m-call-full"));
    }

    [Fact]
    public void Desktop_keeps_the_draggable_window()
    {
        using var ctx = Context(out var calls, out _);
        SetCurrent(calls, ActiveCall());
        var cut = ctx.Render<GlobalCallOverlay>();
        Assert.Empty(cut.FindAll(".m-call-full"));
        Assert.NotEmpty(cut.FindAll(".callx-window .callx-resize"));
        Assert.Single(ctx.JSInterop.Invocations["ipopCallDrag.attach"]);
    }

    [Fact]
    public void Turning_into_a_phone_mid_call_detaches_the_drag()
    {
        using var ctx = Context(out var calls, out var phone);
        SetCurrent(calls, ActiveCall());
        var cut = ctx.Render<GlobalCallOverlay>();
        Assert.Single(ctx.JSInterop.Invocations["ipopCallDrag.attach"]);

        phone.OnPhoneChanged(true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".m-call-full")));
        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations["ipopCallDrag.detach"]));
    }

    private static CallRoomDto ActiveCall() => new(
        Guid.NewGuid(), CallKind.User, CallStatus.Active, null, Guid.NewGuid(),
        DateTime.UtcNow, DateTime.UtcNow, null, null,
        [
            new CallParticipantDto(Guid.NewGuid(), "100", "Jane Kurniawan", CallParticipantStatus.Accepted, false, false, null),
            new CallParticipantDto(Guid.NewGuid(), "200", "Budi Santoso", CallParticipantStatus.Accepted, false, false, null),
        ]);

    private static void SetCurrent(CallState calls, CallRoomDto room) =>
        typeof(CallState).GetProperty(nameof(CallState.Current))!.SetValue(calls, room);

    private static BunitContext Context(out CallState calls, out PhoneState phone)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new NotFound()) { BaseAddress = new Uri("http://localhost/") };
        var js = ctx.JSInterop.JSRuntime;
        var auth = new AuthState(http, js);
        calls = new CallState(auth, http, js);
        phone = new PhoneState(js);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(calls);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(phone);
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
