using Bunit;
using IPop.Web.Components.Phone.Notes;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Paket C (Notes stabilisation) regressions that live in the Blazor page and its stylesheet.
public class NotesStabilityTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IPop.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    /// Phone: a whiteboard's Page actions offered "Style", which does nothing for a board (the page never told the sheet it was one).
    [Fact]
    public void Phone_more_sheet_hides_Style_for_a_whiteboard_and_the_page_passes_the_kind()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<OverlayStack>();
        var board = ctx.Render<NotesMoreSheet>(p => p.Add(x => x.Open, true).Add(x => x.CanEdit, true).Add(x => x.IsBoard, true));
        Assert.Empty(board.FindAll("button[data-action=style]"));
        var page = ctx.Render<NotesMoreSheet>(p => p.Add(x => x.Open, true).Add(x => x.CanEdit, true));
        Assert.Single(page.FindAll("button[data-action=style]"));

        var razor = RepoFile("src/IPop.Web/Pages/Notes.razor");
        var sheet = razor[razor.IndexOf("<NotesMoreSheet", StringComparison.Ordinal)..];
        sheet = sheet[..sheet.IndexOf("/>", StringComparison.Ordinal)];
        Assert.Contains(@"IsBoard=""@(note.Kind == ""board"")""", sheet);
    }

    /// "Restore this version?" (Version history) and "Delete this comment?" (bubble popover, phone comments sheet) opened at
    /// z-index 91, underneath the Notes layers (1060+) and the phone sheet (181): invisible, so the button seemed to do nothing.
    [Fact]
    public void Confirms_asked_from_Notes_layers_and_sheets_open_above_them()
    {
        var css = RepoFile("src/IPop.Web/wwwroot/css/v6/notes.css");
        Assert.Contains("body:has(.nt-layer, .nt-bubble-pop, .m-notes-comments) .confirm-center { z-index: 1181; }", css);
        Assert.Contains("body:has(.nt-layer, .nt-bubble-pop, .m-notes-comments) .confirm-backdrop { z-index: 1180; }", css);
        // above every Notes layer: .nt-layer 1060, .nt-pop 1070, .nt-bubble-pop 1075, .nt-sym-hint 1080, lightbox 1100
        Assert.DoesNotMatch(@"\.nt-[a-z-]+\s*\{[^}]*z-index:\s*1(1[89]\d|[2-9]\d\d)\b", css);
    }

    /// The owner changing someone's share level pushes "noteAccessChanged"; the page reopens with the new level (no reload).
    [Fact]
    public void Share_level_change_is_handled_live()
    {
        var page = RepoFile("src/IPop.Web/Pages/Notes.razor.cs");
        Assert.Contains("public async Task OnAccessChanged()", page);
        var editor = RepoFile("tools/notes-editor/src/index.js");
        Assert.Contains("hub.on('noteAccessChanged'", editor);
        Assert.Contains("call('OnAccessChanged')", editor);
    }
}
