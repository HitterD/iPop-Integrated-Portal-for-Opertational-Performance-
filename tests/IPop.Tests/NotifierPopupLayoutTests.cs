using IPop.Application.Notifier;
using Xunit;

namespace IPop.Tests;

/// 1.4.2: no limit on popups ("20 popups = 20 windows"); every window stays on screen and visible.
public class NotifierPopupLayoutTests
{
    // 1920×1040 working area at 100 %, popup 440×560, as in PopupManager (gap 14, step 30, lift 160).
    private static (int X, int Y) Slot(int i, bool top = false, int w = 1920, int h = 1040) =>
        PopupLayout.CascadeSlot(i, 440, 560, 0, 0, w, h, 14, 30, 160, top);

    [Theory]
    [InlineData(false, 1920, 1040)]
    [InlineData(true, 1920, 1040)]
    [InlineData(false, 1366, 728)]
    [InlineData(false, 1280, 680)]
    public void Twenty_popups_all_stay_fully_on_screen_and_none_hides_another(bool top, int w, int h)
    {
        var slots = Enumerable.Range(0, 20).Select(i => Slot(i, top, w, h)).ToList();
        Assert.All(slots, p => Assert.True(p.X >= 0 && p.Y >= 0 && p.X + 440 <= w && p.Y + 560 <= h, $"{p} off screen"));
        Assert.Equal(20, slots.Distinct().Count());
    }

    [Fact]
    public void First_window_sits_where_it_always_did_and_the_next_cascade_down()
    {
        Assert.Equal((1920 - 440 - 14, 1040 - 560 - 14 - 160), Slot(0));
        var (x1, y1) = Slot(1);
        Assert.Equal((Slot(0).X - 10, Slot(0).Y + 30), (x1, y1));
    }

    [Fact]
    public void When_a_column_is_full_the_next_starts_further_left_from_the_top()
    {
        // Column 0 (bottom-right, lifted 160): room for 160/30 + 1 = 6 windows.
        var first = Enumerable.Range(0, 7).Select(i => Slot(i)).ToList();
        Assert.True(first[5].Y + 560 <= 1040);
        Assert.Equal(14, first[6].Y);                       // new column from the top edge
        Assert.True(first[6].X + 440 <= first[5].X);        // and fully left of the first column's windows
    }

    [Fact]
    public void A_small_screen_wraps_round_with_an_offset_instead_of_stacking_exactly()
    {
        var slots = Enumerable.Range(0, 30).Select(i => Slot(i, false, 900, 700)).ToList();
        Assert.All(slots, p => Assert.True(p.X >= 0 && p.X + 440 <= 900 && p.Y >= 0 && p.Y + 560 <= 700));
        // A screen this small has room for one column of 5; three offset rounds give 15 distinct places, then repeat.
        Assert.Equal(15, slots.Take(15).Distinct().Count());
    }

    [Theory]
    [InlineData(6, 1040, true)]     // 6×150 + 5×10 = 950 ≤ 1012
    [InlineData(7, 1040, false)]
    [InlineData(2, 300, false)]
    public void The_stack_shows_as_many_cards_as_fit(int count, int height, bool fits) =>
        Assert.Equal(fits, PopupLayout.StackFits(count, height, 14, 10, 150));
}
