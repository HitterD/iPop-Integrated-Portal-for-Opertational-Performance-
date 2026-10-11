using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using IPop.Application.MeetingRooms;
using IPop.Web.Components.Admin;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

[Collection("AvatarPhotos")]
public class AdminMeetingRoomsTests
{
    [Fact]
    public void Five_switches_follow_hall_order_and_explicit_aria_strings()
    {
        using var ctx = Context(new Api());
        var cut = ctx.Render<AdminMeetingRooms>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[role=switch]").Count));
        Assert.Equal(["Video in Robusta", "Video in Arabika", "Video in Kalosi", "Video in Kapal Api", "Video in Toraja"],
            cut.FindAll("[role=switch]").Select(e => e.GetAttribute("aria-label")));
        Assert.Equal("false", cut.FindAll("[role=switch]")[2].GetAttribute("aria-checked"));
        Assert.Equal("true", cut.FindAll("[role=switch]")[0].GetAttribute("aria-checked"));
        Assert.Contains("6 people", cut.Markup);
        Assert.Contains("Empty", cut.Markup);
    }

    [Fact]
    public void Switch_sends_allowed_and_updates_only_on_success()
    {
        var api = new Api();
        using var ctx = Context(api);
        var cut = ctx.Render<AdminMeetingRooms>();
        cut.FindAll("[role=switch]")[2].Click();
        cut.WaitForAssertion(() => Assert.Equal("true", cut.FindAll("[role=switch]")[2].GetAttribute("aria-checked")));
        Assert.Equal(("PUT", "/api/admin/meeting-rooms/kalosi/video", "{\"allowed\":true}"), Assert.Single(api.Writes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retention_saves_on_blur_or_enter(bool enter)
    {
        var api = new Api();
        using var ctx = Context(api);
        var cut = ctx.Render<AdminMeetingRooms>();
        cut.Find("input").Input("30");
        if (enter) cut.Find("input").KeyDown("Enter"); else cut.Find("input").Blur();
        cut.WaitForAssertion(() => Assert.Equal(("PUT", "/api/admin/meeting-rooms/retention", "{\"days\":30}"), Assert.Single(api.Writes)));
        cut.Find("input").Blur(); // Enter followed by blur must not double-save.
        Assert.Single(api.Writes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3651")]
    [InlineData("abc")]
    [InlineData("")]
    public void Invalid_retention_sends_nothing(string value)
    {
        var api = new Api();
        using var ctx = Context(api);
        var cut = ctx.Render<AdminMeetingRooms>();
        cut.Find("input").Input(value);
        cut.Find("input").Blur();
        Assert.Contains("Use 1 to 3650 days.", cut.Markup);
        Assert.Empty(api.Writes);
    }

    [Theory]
    [InlineData("online", "Online")]
    [InlineData("offline", "Offline")]
    [InlineData("not_configured", "Not configured")]
    public void Shows_service_status_and_real_storage_location(string status, string text)
    {
        using var ctx = Context(new Api { Egress = status });
        var cut = ctx.Render<AdminMeetingRooms>();
        Assert.Contains(text, cut.Find(".amr-service").TextContent);
        Assert.Contains("NAS ict, SJA SPJ / ICT / recordings", cut.Markup);
        Assert.DoesNotContain("412 GB", cut.Markup); // No fabricated capacity telemetry.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_requires_confirmation(bool accept)
    {
        var api = new Api();
        using var ctx = Context(api);
        var cut = ctx.Render<AdminMeetingRooms>();
        cut.Find(".amr-delete").Click();
        var confirm = ctx.Services.GetRequiredService<ConfirmService>();
        Assert.NotNull(confirm.Current);
        Assert.Empty(api.Writes);
        await cut.InvokeAsync(() => confirm.Resolve(accept));
        cut.WaitForAssertion(() => Assert.Equal(accept ? 0 : 1, cut.FindAll(".amr-recording").Count));
        if (accept) Assert.Equal(("DELETE", $"/api/admin/meeting-rooms/recordings/{api.Recording.Id}", ""), Assert.Single(api.Writes));
        else Assert.Empty(api.Writes);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Stop the recording first.")]
    [InlineData(HttpStatusCode.InternalServerError, "Could not delete the recording. Try again.")]
    [InlineData(HttpStatusCode.Forbidden, "Could not delete the recording. Try again.")]
    public async Task Failed_delete_keeps_row_and_explains_failure(HttpStatusCode status, string message)
    {
        var api = new Api { WriteStatus = status };
        using var ctx = Context(api);
        var cut = ctx.Render<AdminMeetingRooms>();
        cut.Find(".amr-delete").Click();
        await cut.InvokeAsync(() => ctx.Services.GetRequiredService<ConfirmService>().Resolve(true));
        cut.WaitForAssertion(() => Assert.Contains(ctx.Services.GetRequiredService<ToastService>().Active, t => t.Message == message));
        Assert.Single(cut.FindAll(".amr-recording"));
    }

    [Fact]
    public void Failed_switch_keeps_previous_value()
    {
        var api = new Api { WriteStatus = HttpStatusCode.InternalServerError };
        using var ctx = Context(api);
        var cut = ctx.Render<AdminMeetingRooms>();
        cut.FindAll("[role=switch]")[2].Click();
        cut.WaitForAssertion(() => Assert.Single(api.Writes));
        Assert.Equal("false", cut.FindAll("[role=switch]")[2].GetAttribute("aria-checked"));
        Assert.NotEmpty(ctx.Services.GetRequiredService<ToastService>().Active);
    }

    [Fact]
    public void Stepper_saves_one_day_changes()
    {
        var api = new Api();
        using var ctx = Context(api);
        var cut = ctx.Render<AdminMeetingRooms>();
        cut.Find("[aria-label='Fewer days']").Click();
        cut.WaitForAssertion(() => Assert.Equal("{\"days\":89}", Assert.Single(api.Writes).Body));
        cut.Find("[aria-label='More days']").Click();
        cut.WaitForAssertion(() => Assert.Equal("{\"days\":90}", api.Writes[1].Body));
    }

    [Theory]
    [InlineData(1, "Fewer days")]
    [InlineData(3650, "More days")]
    public void Stepper_disables_at_limits(int days, string label)
    {
        using var ctx = Context(new Api { Days = days });
        var cut = ctx.Render<AdminMeetingRooms>();
        Assert.True(cut.Find($"[aria-label='{label}']").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public void Failed_retention_does_not_claim_saved_and_can_be_retried(HttpStatusCode status, bool throws)
    {
        var api = new Api { WriteStatus = status, ThrowOnWrite = throws };
        using var ctx = Context(api);
        var cut = ctx.Render<AdminMeetingRooms>();
        cut.Find("input").Input("30");
        cut.Find("input").Blur();
        cut.WaitForAssertion(() => Assert.Equal("90", cut.Find("input").GetAttribute("value")));
        Assert.Single(api.Writes);
        cut.Find("input").Blur();
        Assert.Single(api.Writes); // The restored accepted value needs no save.
        cut.Find("input").Input("30");
        cut.Find("input").Blur();
        cut.WaitForAssertion(() => Assert.Equal(2, api.Writes.Count));
        Assert.Equal("90", cut.Find("input").GetAttribute("value"));
        if (status == HttpStatusCode.BadRequest) Assert.Contains("Use 1 to 3650 days.", cut.Markup);
        else Assert.NotEmpty(ctx.Services.GetRequiredService<ToastService>().Active);
    }

    private static BunitContext Context(Api api)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(new AuthState(http, ctx.JSInterop.JSRuntime));
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton(new ConfirmService());
        return ctx;
    }

    private sealed class Api : HttpMessageHandler
    {
        public bool ThrowOnWrite { get; init; }
        public int Days { get; init; } = 90;
        public string Egress { get; init; } = "online";
        public HttpStatusCode WriteStatus { get; init; } = HttpStatusCode.OK;
        public List<(string Method, string Path, string Body)> Writes { get; } = [];
        public MeetingRecordingDto Recording { get; } = new(Guid.NewGuid(), "robusta", "Robusta", "Weekly ICT sync", "Budi",
            DateTime.UtcNow.AddDays(-1), 600, 123456, 6, "ready", DateTime.UtcNow.AddDays(89));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path == "/api/admin/meeting-rooms")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                    video = new Dictionary<string, bool> { ["robusta"] = true, ["arabika"] = true, ["kalosi"] = false, ["kapal-api"] = true, ["toraja"] = true },
                    retentionDays = Days, egress = Egress, recordings = new[] { Recording } }) };
            if (req.Method == HttpMethod.Get && path == "/api/meeting-rooms")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] {
                    new MeetingRoomDto("arabika", "Arabika", 20, true, true,
                        Enumerable.Range(0, 6).Select(i => new MeetingRoomPerson(Guid.NewGuid(), $"Person {i}", false, false)).ToList(), null, null, null) }) };
            Writes.Add((req.Method.Method, path, req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct)));
            if (ThrowOnWrite) throw new HttpRequestException("Test network failure");
            return new(WriteStatus);
        }
    }
}
