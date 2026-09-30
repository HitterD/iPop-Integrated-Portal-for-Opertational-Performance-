using System.Net;
using Bunit;
using IPop.Application.Calls;
using IPop.Web.Components.Calls;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// The in-call window/pill hand their element to js/call-drag.js; the JS owns the inline
/// position, so the markup must never render a style attribute on them.
[Collection("AvatarPhotos")]
public class CallOverlayDragTests
{
    [Fact]
    public void Window_attaches_drag_and_minimizing_reattaches_the_pill()
    {
        using var ctx = Context(out var calls);
        SetCurrent(calls, ActiveCall());

        var cut = ctx.Render<GlobalCallOverlay>();

        var window = cut.Find(".callx-window");
        Assert.Null(window.GetAttribute("style"));
        Assert.NotEmpty(cut.FindAll(".callx-window .callx-resize"));
        Assert.NotEmpty(cut.FindAll(".callx-window .callx-head.callx-drag-handle"));
        var attach = Assert.Single(ctx.JSInterop.Invocations["ipopCallDrag.attach"]);
        Assert.Equal("window", attach.Arguments[1]);

        cut.Find(".callx-head .callx-icon").Click();   // Perkecil

        Assert.Null(cut.Find(".callx-pill").GetAttribute("style"));
        cut.WaitForAssertion(() => Assert.Equal(2, ctx.JSInterop.Invocations["ipopCallDrag.attach"].Count));
        Assert.Equal("pill", ctx.JSInterop.Invocations["ipopCallDrag.attach"][1].Arguments[1]);

        // Re-renders of the same element (timer ticks) must not re-attach.
        cut.Render();
        Assert.Equal(2, ctx.JSInterop.Invocations["ipopCallDrag.attach"].Count);
    }

    [Fact]
    public void Leaving_the_call_detaches_and_rings_never_attach()
    {
        using var ctx = Context(out var calls);
        SetCurrent(calls, ActiveCall());
        var cut = ctx.Render<GlobalCallOverlay>();
        Assert.Single(ctx.JSInterop.Invocations["ipopCallDrag.attach"]);

        calls.Clear();

        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations["ipopCallDrag.detach"]));
        Assert.Empty(cut.FindAll(".callx-window, .callx-pill"));
    }

    [Fact]
    public void Add_person_button_renders_for_active_calls_and_opens_the_picker()
    {
        using var ctx = Context(out var calls);
        SetCurrent(calls, ActiveCall());
        SetMe(calls, "100");

        var cut = ctx.Render<GlobalCallOverlay>();

        var add = cut.Find(".callx-controls .callx-add-person");
        Assert.Contains("Add person", add.TextContent);
        Assert.NotNull(add.QuerySelector("[data-lucide='user-plus']"));
        Assert.Empty(cut.FindAll(".callx-add"));
        add.Click();
        Assert.NotEmpty(cut.FindAll(".callx-add input[aria-label='Search people to add']"));
        Assert.Null(cut.Find(".callx-window").GetAttribute("style"));

        // Also in a group/ad-hoc call window.
        SetCurrent(calls, ActiveCall() with { Kind = CallKind.AdHoc });
        cut.Render();
        Assert.NotEmpty(cut.FindAll(".callx-group-window .callx-add-person"));
    }

    [Fact]
    public void Add_person_is_hidden_while_the_1to1_call_still_rings()
    {
        using var ctx = Context(out var calls);
        var ringing = ActiveCall() with { Status = CallStatus.Ringing, EstablishedAt = null };
        SetCurrent(calls, ringing with { CreatorId = ringing.Participants[0].UserId });
        SetMe(calls, "100");

        var cut = ctx.Render<GlobalCallOverlay>();

        Assert.NotEmpty(cut.FindAll(".callx-window"));
        Assert.Empty(cut.FindAll(".callx-add-person"));
    }

    private static void SetMe(CallState calls, string nik)
    {
        var auth = (AuthState)typeof(CallState).GetField("_auth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(calls)!;
        var me = System.Text.Json.JsonSerializer.Deserialize<DirectoryEmployeeDto>($$"""{"nik":"{{nik}}","name":"Jane Kurniawan"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth, me);
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

    private static BunitContext Context(out CallState calls)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new NotFound()) { BaseAddress = new Uri("http://localhost/") };
        var js = ctx.JSInterop.JSRuntime;
        var auth = new AuthState(http, js);
        calls = new CallState(auth, http, js);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(calls);
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
