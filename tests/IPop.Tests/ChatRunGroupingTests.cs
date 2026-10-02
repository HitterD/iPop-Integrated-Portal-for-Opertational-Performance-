using IPop.Application.Chat;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// Same-sender runs drive the bubble corners and the tail (grp-first/mid/last/single).
public class ChatRunGroupingTests
{
    private static readonly DateTime T0 = new(2026, 10, 2, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_sender_within_window_continues_the_run()
    {
        var a = Msg("U1", T0);
        var b = Msg("U1", T0.AddMinutes(2));
        Assert.True(ChatFormatters.ContinuesRun(a, b, null));
    }

    [Fact]
    public void Run_breaks_on_other_sender_gap_divider_and_system_rows()
    {
        var a = Msg("U1", T0);
        Assert.False(ChatFormatters.ContinuesRun(null, a, null));
        Assert.False(ChatFormatters.ContinuesRun(a, Msg("U2", T0.AddMinutes(1)), null));
        Assert.False(ChatFormatters.ContinuesRun(a, Msg("U1", T0.AddMinutes(6)), null));
        var afterDivider = Msg("U1", T0.AddMinutes(1));
        Assert.False(ChatFormatters.ContinuesRun(a, afterDivider, afterDivider.Id));
        Assert.False(ChatFormatters.ContinuesRun(a, Msg("U1", T0.AddMinutes(1), "system"), null));
        Assert.False(ChatFormatters.ContinuesRun(Msg("U1", T0, "emergency"), Msg("U1", T0.AddMinutes(1)), null));
    }

    [Theory]
    [InlineData(false, false, "grp-single")]
    [InlineData(false, true, "grp-first")]
    [InlineData(true, true, "grp-mid")]
    [InlineData(true, false, "grp-last")]
    public void Run_position_maps_to_class(bool prev, bool next, string expected) =>
        Assert.Equal(expected, ChatFormatters.RunClass(prev, next));

    [Fact]
    public void Three_message_burst_reads_first_mid_last()
    {
        var list = new[] { Msg("U1", T0), Msg("U1", T0.AddMinutes(1)), Msg("U1", T0.AddMinutes(2)), Msg("U2", T0.AddMinutes(3)) };
        var classes = list.Select((m, i) => ChatFormatters.RunClass(
            ChatFormatters.ContinuesRun(i > 0 ? list[i - 1] : null, m, null),
            i + 1 < list.Length && ChatFormatters.ContinuesRun(m, list[i + 1], null))).ToArray();
        Assert.Equal(["grp-first", "grp-mid", "grp-last", "grp-single"], classes);
    }

    private static MessageDto Msg(string nik, DateTime at, string type = "text") => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), nik, nik, type, "hi",
        at, null, false, null, null, null, null, 0, null, [], [], [], false, Guid.NewGuid());
}
