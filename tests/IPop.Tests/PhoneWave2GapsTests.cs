using System.Net;
using Bunit;
using IPop.Web.Components.Calls;
using IPop.Web.Components.Mail;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

[Collection("AvatarPhotos")]
public class PhoneWave2GapsTests
{
    private static BunitContext Context(bool isPhone)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new NotFound()) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        var calls = new CallState(auth, http, ctx.JSInterop.JSRuntime);
        var phone = new PhoneState(ctx.JSInterop.JSRuntime);
        phone.OnPhoneChanged(isPhone);
        ctx.Services.AddSingleton(phone);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(calls);
        ctx.Services.AddSingleton(new MailService(auth, http));
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton(new ConfirmService());
        ctx.Services.AddSingleton(new MeetingRoomState(auth, ctx.JSInterop.JSRuntime, calls));
        ctx.Services.AddScoped<OverlayStack>();
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }

    [Fact]
    public void Phone_compose_has_cancel_new_mail_send_and_keyboard_toolbar()
    {
        using var ctx = Context(true);
        var closed = false;
        var cut = ctx.Render<MailComposer>(p => p.Add(x => x.IsOpen, true).Add(x => x.IsOpenChanged, v => closed = !v));
        Assert.Contains("New mail", cut.Find(".m-mail-compose-nav").TextContent);
        Assert.Single(cut.FindAll(".m-mail-compose-nav button[aria-label='Send']"));
        Assert.Single(cut.FindAll(".mrte-bar button[aria-label='From NAS']"));
        Assert.Single(cut.FindAll(".mrte-bar button[aria-label='Photo']"));
        Assert.Single(cut.FindAll(".mrte-bar button[aria-label='Attach file']"));
        Assert.Single(cut.FindAll(".mrte-bar button[aria-label='Schedule send']"));
        Assert.Single(cut.FindAll(".mc-sheet")); // version-check busy detector
        cut.Find(".m-mail-compose-nav button[aria-label='Cancel']").Click();
        Assert.True(closed);
    }

    [Fact]
    public void Desktop_compose_has_no_phone_navigation_or_toolbar_slots()
    {
        using var ctx = Context(false);
        var cut = ctx.Render<MailComposer>(p => p.Add(x => x.IsOpen, true));
        Assert.Empty(cut.FindAll(".m-mail-compose-nav, .m-mail-tools"));
        Assert.Single(cut.FindAll(".head-actions button[aria-label='Minimise']"));
    }

    [Fact]
    public async Task Phone_meeting_chat_uses_kit_sheet_and_Back_closes_it_without_unmounting_media()
    {
        using var ctx = Context(true);
        var rooms = ctx.Services.GetRequiredService<MeetingRoomState>();
        rooms.SetForTest(new("kalosi", "Kalosi", "qa-w2-kalosi", "ws://localhost:1", "t", true, "me"));
        var cut = ctx.Render<MeetingRoomOverlay>();
        Assert.Single(cut.FindAll(".m-meet-top button[aria-label='Participants']"));
        cut.Find(".mr-chat-btn").Click();
        Assert.Single(cut.FindAll(".ph-sheet.m-meet-chat"));
        Assert.Single(cut.FindAll("#mr-stage"));
        Assert.Single(cut.FindAll("#mr-strip"));
        await cut.InvokeAsync(() => ctx.Services.GetRequiredService<OverlayStack>().CloseTopAsync());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".m-meet-chat")));
        Assert.False(rooms.ChatOpen);
        Assert.Single(cut.FindAll("#mr-stage"));
    }

    [Fact]
    public void Phone_record_menu_is_in_participants_not_the_hidden_desktop_dock()
    {
        using var ctx = Context(true);
        ctx.Services.GetRequiredService<MeetingRoomState>().SetForTest(new("kalosi", "Kalosi", "qa-w2-kalosi", "ws://localhost:1", "t", true, "me"));
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find(".m-meet-top button[aria-label='Participants']").Click();
        cut.Find(".m-meet-record").Click();
        Assert.Single(cut.FindAll(".m-meet-people .mr-menu"));
        Assert.Empty(cut.FindAll(".mr-dock .mr-menu"));
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
