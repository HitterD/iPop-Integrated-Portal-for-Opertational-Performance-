namespace IPop.Application.Notifier;

/// Where iPop Notifier popups go on screen (1.4.2: no limit on popups). Pure arithmetic in device pixels, so it can
/// be tested without Windows; Windows/PopupManager.cs passes the working area and its DPI-scaled steps.
public static class PopupLayout
{
    /// Where the <paramref name="index"/>-th popup window goes. Each window steps one <paramref name="step"/> down (and
    /// a third of it left) from its column's first slot, so the title bar of every window behind stays visible. The
    /// first column starts where 1.4.1 started (bottom-right: lifted above the corner); when the next step would leave
    /// the working area, a new column starts one window width (+ its drift) further left, from the top edge, using the
    /// full height. When every column is full the pattern starts again shifted by a third of a step, so windows never
    /// land exactly on top of each other. Every window stays on screen.
    public static (int X, int Y) CascadeSlot(int index, int width, int height, int areaLeft, int areaTop, int areaWidth, int areaHeight,
        int gap, int step, int lift, bool topRight)
    {
        var areaRight = areaLeft + areaWidth;
        var areaBottom = areaTop + areaHeight;
        step = Math.Max(1, step);
        var top = areaTop + gap;
        var bottom = areaBottom - height;                     // lowest top edge that keeps the window on screen
        int First(int col) => col == 0 && !topRight ? Math.Max(areaTop, areaBottom - height - gap - lift) : top;
        int Fit(int col) => Math.Max(1, (bottom - First(col)) / step + 1);
        var dx = Math.Max(1, step / 3);
        var maxFit = Math.Max(1, (bottom - top) / step + 1);
        // Columns never overlap: one window width plus the column's whole leftward drift apart.
        var colWidth = width + gap + (maxFit - 1) * dx;
        var columns = Math.Max(1, (areaWidth - gap) / Math.Max(1, colWidth));
        var perRound = 0;
        for (var c = 0; c < columns; c++) perRound += Fit(c);
        var round = index / perRound;
        var left = index % perRound;
        var colIdx = 0;
        while (left >= Fit(colIdx)) { left -= Fit(colIdx); colIdx++; }
        var offset = round * step / 3 % step;
        var x = areaRight - width - gap - colIdx * colWidth - left * dx - offset;
        var y = First(colIdx) + left * step + offset;
        x = Math.Clamp(x, areaLeft, Math.Max(areaLeft, areaRight - width));
        y = Math.Clamp(y, areaTop, Math.Max(areaTop, areaBottom - height));
        return (x, y);
    }

    /// v1 stack: <paramref name="count"/> cards of at least <paramref name="minHeight"/> fit in <paramref name="height"/>
    /// (screen edge margins and the gaps between cards included). Only cards that don't fit wait behind "+N more".
    public static bool StackFits(int count, int height, int edge, int gap, int minHeight) =>
        count * minHeight + (count - 1) * gap <= height - 2 * edge;
}
