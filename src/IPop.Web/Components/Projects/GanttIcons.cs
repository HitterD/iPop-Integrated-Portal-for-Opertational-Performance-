using Microsoft.AspNetCore.Components;

namespace IPop.Web.Components.Projects;

/// Inline SVG icons for the Gantt (same glyphs as the mockup). Inline, because Blazor owns this markup and
/// lucide.createIcons() would swap nodes under it.
public static class GanttIcons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["chev"] = "<polyline points=\"6 9 12 15 18 9\"/>",
        ["flag"] = "<path d=\"M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z\"/><line x1=\"4\" y1=\"22\" x2=\"4\" y2=\"15\"/>",
        ["pin"] = "<line x1=\"12\" y1=\"17\" x2=\"12\" y2=\"22\"/><path d=\"M5 17h14v-1.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V6h1a2 2 0 0 0 0-4H8a2 2 0 0 0 0 4h1v4.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24z\"/>",
        ["max"] = "<polyline points=\"15 3 21 3 21 9\"/><polyline points=\"9 21 3 21 3 15\"/><line x1=\"21\" y1=\"3\" x2=\"14\" y2=\"10\"/><line x1=\"3\" y1=\"21\" x2=\"10\" y2=\"14\"/>",
        ["target"] = "<circle cx=\"12\" cy=\"12\" r=\"10\"/><circle cx=\"12\" cy=\"12\" r=\"6\"/><circle cx=\"12\" cy=\"12\" r=\"2\"/>",
        ["download"] = "<path d=\"M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4\"/><polyline points=\"7 10 12 15 17 10\"/><line x1=\"12\" y1=\"15\" x2=\"12\" y2=\"3\"/>",
        ["upload"] = "<path d=\"M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4\"/><polyline points=\"17 8 12 3 7 8\"/><line x1=\"12\" y1=\"3\" x2=\"12\" y2=\"15\"/>",
        ["search"] = "<circle cx=\"11\" cy=\"11\" r=\"8\"/><line x1=\"21\" y1=\"21\" x2=\"16.65\" y2=\"16.65\"/>",
        ["check"] = "<polyline points=\"20 6 9 17 4 12\"/>",
        ["alert"] = "<path d=\"M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z\"/><line x1=\"12\" y1=\"9\" x2=\"12\" y2=\"13\"/><line x1=\"12\" y1=\"17\" x2=\"12.01\" y2=\"17\"/>",
        ["x"] = "<line x1=\"18\" y1=\"6\" x2=\"6\" y2=\"18\"/><line x1=\"6\" y1=\"6\" x2=\"18\" y2=\"18\"/>",
        ["table"] = "<rect x=\"3\" y=\"3\" width=\"18\" height=\"18\" rx=\"2\"/><line x1=\"3\" y1=\"9\" x2=\"21\" y2=\"9\"/><line x1=\"3\" y1=\"15\" x2=\"21\" y2=\"15\"/><line x1=\"9\" y1=\"3\" x2=\"9\" y2=\"21\"/>",
        ["gantt"] = "<line x1=\"3\" y1=\"4\" x2=\"3\" y2=\"20\"/><rect x=\"6\" y=\"5\" width=\"9\" height=\"3\" rx=\"1\"/><rect x=\"9\" y=\"11\" width=\"10\" height=\"3\" rx=\"1\"/><rect x=\"7\" y=\"17\" width=\"6\" height=\"3\" rx=\"1\"/>",
        ["trash"] = "<polyline points=\"3 6 5 6 21 6\"/><path d=\"M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6\"/><path d=\"M10 11v6M14 11v6\"/>",
    };

    public static MarkupString Svg(string name, string cls = "")
        => new($"<svg class=\"pv2g-i {cls}\" viewBox=\"0 0 24 24\" aria-hidden=\"true\">{(Paths.TryGetValue(name, out var p) ? p : "")}</svg>");
}
