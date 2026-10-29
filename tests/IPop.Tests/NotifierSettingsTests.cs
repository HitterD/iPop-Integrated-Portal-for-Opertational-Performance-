using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Application.Notifier;
using Xunit;

namespace IPop.Tests;

/// iPop Notifier settings (ui-prefs key "notifier"): defaults match the approved mockups (v2 windows style,
/// chat as toast), unknown values fall back, and only the normalised form is stored.
public class NotifierSettingsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Defaults_are_windows_style_popups_on_chat_toast()
    {
        using var t = new TestDb();
        var u = t.AddUser("NS1");
        var (status, json) = await UiPrefsEndpoints.GetAsync(t.Db, u.Id, "notifier", false, default);
        Assert.Equal(200, status);
        var s = NotifierSettings.Parse(json);
        Assert.Equal("windows", s.Style);
        Assert.Equal("on", s.Popups);
        Assert.Equal("hold", s.DuringMeetings);
        Assert.Equal("toast", s.DeliveryFor("chat"));
        Assert.Equal("popup", s.DeliveryFor("mail"));
        Assert.Equal("popup", s.DeliveryFor("tickets"));
        Assert.Equal("toast", s.DeliveryFor("tickets", "ticket.message_created"));   // iDesk chat: toast with a reply box
        Assert.Equal("popup", s.DeliveryFor("tickets", "ticket.assigned"));
        Assert.True(s.StartWithWindows);
    }

    [Fact]
    public async Task Saving_stores_the_normalised_form_and_follows_the_account()
    {
        using var t = new TestDb();
        var u = t.AddUser("NS2");
        var body = J("""{"style":"stack","popups":"until","offUntil":"2026-10-19T08:00:00Z","maxVisible":99,"volume":-5,"modules":{"chat":"popup","mail":"toast","bogus":"popup"},"evil":"<script>"}""");
        Assert.Equal(204, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "notifier", body, false, default));
        var (_, json) = await UiPrefsEndpoints.GetAsync(t.Verify(), u.Id, "notifier", false, default);
        Assert.DoesNotContain("evil", json);
        Assert.DoesNotContain("bogus", json);
        var s = NotifierSettings.Parse(json);
        Assert.Equal("stack", s.Style);
        Assert.Equal("until", s.Popups);
        Assert.NotNull(s.OffUntil);
        Assert.Equal(10, s.MaxVisible);
        Assert.Equal(0, s.Volume);
        Assert.Equal("toast", s.DeliveryFor("chat")); // chat can never be a popup window
        Assert.Equal("toast", s.DeliveryFor("mail"));
    }

    [Fact]
    public async Task Appearance_defaults_to_system_and_keeps_light_or_dark()
    {
        using var t = new TestDb();
        var u = t.AddUser("NS4");
        Assert.Equal("system", NotifierSettings.Parse(null).Theme);
        Assert.Equal("system", NotifierSettings.Parse("""{"theme":"neon"}""").Theme);
        Assert.Equal(204, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "notifier", J("""{"theme":"dark","textSize":"large"}"""), false, default));
        var (_, json) = await UiPrefsEndpoints.GetAsync(t.Verify(), u.Id, "notifier", false, default);
        var s = NotifierSettings.Parse(json);
        Assert.Equal("dark", s.Theme);
        Assert.Equal("large", s.TextSize);
        Assert.Equal("light", NotifierSettings.Parse("""{"theme":"light"}""").Theme);
    }

    [Fact]
    public async Task Non_object_body_is_rejected()
    {
        using var t = new TestDb();
        var u = t.AddUser("NS3");
        Assert.Equal(400, await UiPrefsEndpoints.PutAsync(t.Db, u.Id, "notifier", J("[]"), false, default));
    }

    [Theory]
    [InlineData("mail.new", "mail")]
    [InlineData("ticket.message_created", "tickets")]
    [InlineData("meeting.reminder", "calendar")]
    [InlineData("calendar.share.ask", "calendar")]
    [InlineData("chat.mention", "chat")]
    [InlineData("project.task_assigned", "projects")]
    [InlineData("news.published", "news")]
    [InlineData("note.comment", "notes")]
    [InlineData("system.hris_sync_failed", "system")]
    [InlineData(null, "system")]
    public void Event_types_map_to_modules(string? eventType, string module) => Assert.Equal(module, NotifierSettings.ModuleOf(eventType));
}
