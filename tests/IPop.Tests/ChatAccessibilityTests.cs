using IPop.Application.Chat;
using IPop.Web.Pages;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace IPop.Tests;

/// Chat keyboard accessibility: row labels and the polite live announcer (no content leakage,
/// no history flood, coalesced bursts, one-shot failures, throttled typing).
public class ChatAccessibilityTests
{
    private static readonly Guid Room = Guid.NewGuid();
    private const string Secret = "salary slip attached for March";

    private static MessageDto Msg(string sender = "Budi", string type = "text", bool deleted = false, Guid? room = null) =>
        new(Guid.NewGuid(), room ?? Room, Guid.NewGuid(), sender, "N" + sender, type, Secret,
            new DateTime(2026, 10, 2, 3, 4, 0, DateTimeKind.Utc), null, deleted, null, null, null, null, 0, null,
            [], [], [], false, Guid.NewGuid());

    private sealed class Clock { public DateTime Now = new(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc); }

    private static (ChatLiveAnnouncer a, Clock c) Settled()
    {
        var c = new Clock();
        var a = new ChatLiveAnnouncer(() => c.Now);
        a.SetRoom(Room);
        c.Now += ChatLiveAnnouncer.RoomSettle + TimeSpan.FromSeconds(1);
        return (a, c);
    }

    // ---- row label ----

    [Fact]
    public void Row_label_is_sender_and_time_never_content()
    {
        var m = Msg();
        var label = ChatA11yLabels.RowLabel(m, isSent: false, isPending: false, isFailed: false);
        Assert.StartsWith("Budi, ", label);
        Assert.Contains(m.CreatedAt.ToLocalTime().ToString("HH:mm"), label);
        Assert.DoesNotContain("salary", label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Row_label_marks_own_failed_pending_deleted_emergency_and_skips_system()
    {
        Assert.StartsWith("You, ", ChatA11yLabels.RowLabel(Msg(), true, false, false));
        Assert.EndsWith(", failed to send", ChatA11yLabels.RowLabel(Msg(), true, false, true));
        Assert.EndsWith(", sending", ChatA11yLabels.RowLabel(Msg(), true, true, false));
        Assert.EndsWith(", deleted", ChatA11yLabels.RowLabel(Msg(deleted: true), false, false, false));
        Assert.StartsWith("Emergency, Budi", ChatA11yLabels.RowLabel(Msg(type: "emergency"), false, false, false));
        Assert.Equal("", ChatA11yLabels.RowLabel(Msg(type: "system"), false, false, false));
        Assert.StartsWith("Someone, ", ChatA11yLabels.RowLabel(Msg(sender: " "), false, false, false));
    }

    [Fact]
    public void Keyboard_click_is_detected_by_zero_detail_and_coordinates()
    {
        Assert.True(ChatA11yLabels.IsKeyboardClick(new MouseEventArgs { Detail = 0 }));
        Assert.False(ChatA11yLabels.IsKeyboardClick(new MouseEventArgs { Detail = 1, ClientX = 10, ClientY = 10 }));
        Assert.False(ChatA11yLabels.IsKeyboardClick(new MouseEventArgs { Detail = 0, ClientX = 5, ClientY = 7, ScreenX = 5, ScreenY = 7 }));
    }

    // ---- incoming ----

    [Fact]
    public void Incoming_announces_sender_without_content()
    {
        var (a, _) = Settled();
        a.Incoming(Msg(), own: false, quiet: false);
        Assert.Equal("New message from Budi", a.Text.Trim());
        Assert.DoesNotContain("salary", a.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Incoming_is_quiet_for_own_system_deleted_loading_and_right_after_room_open()
    {
        var (a, c) = Settled();
        a.Incoming(Msg(), own: true, quiet: false);
        a.Incoming(Msg(type: "system"), own: false, quiet: false);
        a.Incoming(Msg(deleted: true), own: false, quiet: false);
        a.Incoming(Msg(), own: false, quiet: true);
        Assert.Equal("", a.Text);

        var other = Guid.NewGuid();
        a.Incoming(Msg(room: other), own: false, quiet: false); // room switch: settle window
        Assert.Equal("", a.Text);
        c.Now += ChatLiveAnnouncer.RoomSettle;
        a.Incoming(Msg(room: other), own: false, quiet: false);
        Assert.Equal("New message from Budi", a.Text.Trim());
    }

    [Fact]
    public void Burst_coalesces_into_a_count_and_resets_after_the_window()
    {
        var (a, c) = Settled();
        for (var i = 0; i < 3; i++) { a.Incoming(Msg(), false, false); c.Now += TimeSpan.FromSeconds(1); }
        Assert.Equal("3 new messages from Budi", a.Text.Trim());

        a.Incoming(Msg(sender: "Sari"), false, false);
        Assert.Equal("4 new messages", a.Text.Trim());

        c.Now += ChatLiveAnnouncer.CoalesceWindow + TimeSpan.FromSeconds(1);
        a.Incoming(Msg(sender: "Sari"), false, false);
        Assert.Equal("New message from Sari", a.Text.Trim());
    }

    [Fact]
    public void Identical_consecutive_announcements_still_change_the_region_text()
    {
        var (a, c) = Settled();
        a.Incoming(Msg(), false, false);
        var first = a.Text;
        c.Now += ChatLiveAnnouncer.CoalesceWindow + TimeSpan.FromSeconds(1);
        a.Incoming(Msg(), false, false);
        Assert.NotEqual(first, a.Text);
        Assert.Equal(first.Trim(), a.Text.Trim());
    }

    [Fact]
    public void Emergency_is_named_as_such()
    {
        var (a, _) = Settled();
        a.Incoming(Msg(type: "emergency"), false, false);
        Assert.Equal("Emergency message from Budi", a.Text.Trim());
    }

    // ---- failures ----

    [Fact]
    public void Failure_announces_once_per_message_and_again_after_retry_fails()
    {
        var (a, c) = Settled();
        var id = Guid.NewGuid();
        a.Failures([id], quiet: false);
        Assert.Equal("Message failed to send. Retry is available.", a.Text.Trim());

        var text = a.Text;
        a.Failures([id], quiet: false);
        Assert.Equal(text, a.Text);

        a.Failures([], quiet: false);   // retried
        a.Failures([id], quiet: false); // failed again
        Assert.NotEqual(text, a.Text);

        var b = Guid.NewGuid(); var d = Guid.NewGuid();
        a.Failures([id, b, d], quiet: false);
        Assert.Equal("2 messages failed to send. Retry is available.", a.Text.Trim());
    }

    [Fact]
    public void Failures_present_while_loading_become_baseline_not_announcements()
    {
        var (a, _) = Settled();
        var id = Guid.NewGuid();
        a.Failures([id], quiet: true);
        Assert.Equal("", a.Text);
        a.Failures([id], quiet: false);
        Assert.Equal("", a.Text);
    }

    // ---- typing ----

    [Fact]
    public void Typing_announces_on_start_and_is_throttled()
    {
        var (a, c) = Settled();
        a.Typing(["Budi"], quiet: false);
        Assert.Equal("Budi is typing", a.Text.Trim());

        var text = a.Text;
        a.Typing([], false);
        c.Now += TimeSpan.FromSeconds(3);
        a.Typing(["Budi"], false);
        Assert.Equal(text, a.Text); // within cooldown

        c.Now += ChatLiveAnnouncer.TypingCooldown;
        a.Typing(["Budi", "Sari"], false);
        Assert.Equal("Budi and Sari are typing", a.Text.Trim());

        c.Now += ChatLiveAnnouncer.TypingCooldown;
        a.Typing(["A", "B", "C"], false);
        Assert.Equal("3 people are typing", a.Text.Trim());
    }

    [Fact]
    public void Typing_never_overwrites_a_fresh_message_announcement()
    {
        var (a, c) = Settled();
        a.Incoming(Msg(), false, false);
        c.Now += TimeSpan.FromMilliseconds(500);
        a.Typing(["Sari"], false);
        Assert.Equal("New message from Budi", a.Text.Trim());
    }

    [Fact]
    public void Room_switch_clears_text_and_baselines()
    {
        var (a, _) = Settled();
        a.Incoming(Msg(), false, false);
        Assert.NotEqual("", a.Text);
        a.SetRoom(Guid.NewGuid());
        Assert.Equal("", a.Text);
        a.SetRoom(null);
        Assert.Equal("", a.Text);
    }
}
