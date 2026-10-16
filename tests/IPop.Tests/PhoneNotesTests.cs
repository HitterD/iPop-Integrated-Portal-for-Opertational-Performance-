using Bunit;
using IPop.Application.Notes;
using IPop.Web.Components.Phone.Notes;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class PhoneNotesTests
{
    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }

    [Fact]
    public void List_filters_and_tree_expand_independently_of_opening_a_page()
    {
        using var ctx = Context();
        var root = Page("Planning");
        var child = Page("Checklist", root.Id);
        Guid? opened = null;
        var cut = ctx.Render<NotesListPhone>(p => p.Add(x => x.Sidebar, new NoteSidebarDto([root, child], [root.Id]))
            .Add(x => x.OnOpen, id => opened = id));
        Assert.NotNull(cut.Find("input[aria-label='Search notes']"));
        Assert.DoesNotContain("??", cut.Markup);
        Assert.Equal(new[] { "Recent", "Favorites", "All" }, cut.FindAll("[role=tab]").Select(x => x.TextContent.Trim()));
        cut.Find("[role=tab][data-value=all]").Click();
        Assert.Empty(cut.FindAll($"[data-page='{child.Id}']"));
        cut.Find("button[aria-label='Expand Planning']").Click();
        Assert.Null(opened);
        cut.Find($"[data-page='{child.Id}']").Click();
        Assert.Equal(child.Id, opened);
        Assert.NotNull(cut.Find("button[aria-label='New page']"));
    }

    [Fact]
    public void List_renders_missing_emoji_as_document_without_literal_expression()
    {
        using var ctx = Context();
        var page = Page("No emoji") with { Emoji = null };
        var cut = ctx.Render<NotesListPhone>(p => p.Add(x => x.Sidebar, new NoteSidebarDto([page], [page.Id])));
        Assert.Equal("📄", cut.Find(".m-notes-card > span").TextContent);
        Assert.Equal("📄", cut.Find(".m-notes-page-row > span").TextContent);
        Assert.DoesNotContain("??", cut.Markup);
    }

    [Fact]
    public void More_sheet_hides_owner_and_edit_actions_for_viewers_but_keeps_export_and_history()
    {
        using var ctx = Context();
        var cut = ctx.Render<NotesMoreSheet>(p => p.Add(x => x.Open, true));
        Assert.Empty(cut.FindAll("button[data-action=share],button[data-action=move],button[data-action=delete]"));
        foreach (var action in new[] { "favorite", "comments", "pdf", "md", "html", "history" })
            Assert.NotNull(cut.Find($"button[data-action='{action}']"));
        Assert.DoesNotContain("NIK", cut.Markup);
    }

    [Fact]
    public void More_sheet_for_owner_dispatches_real_actions_and_closes_first()
    {
        using var ctx = Context();
        var events = new List<string>();
        var cut = ctx.Render<NotesMoreSheet>(p => p.Add(x => x.Open, true).Add(x => x.CanEdit, true)
            .Add(x => x.IsOwner, true).Add(x => x.OnAction, action => events.Add(action))
            .Add(x => x.OpenChanged, open => events.Add(open ? "open" : "close")));
        cut.Find("button[data-action=share]").Click();
        Assert.Equal(new[] { "close", "share" }, events);
    }

    [Fact]
    public void Comments_sheet_uses_tall_scale_back_and_existing_overlay_stack()
    {
        using var ctx = Context();
        var cut = ctx.Render<NotesCommentsSheet>(p => p.Add(x => x.Open, true).AddChildContent("Quoted block"));
        Assert.NotNull(cut.Find(".ph-sheet-tall.ph-scale-back"));
        Assert.Contains("Quoted block", cut.Markup);
        Assert.Single(cut.FindComponents<IPop.Web.Components.OverlayHook>());
    }

    private static NoteListItemDto Page(string title, Guid? parent = null) =>
        new(Guid.NewGuid(), parent, title, "📄", "owner", true, false, false, false, 0, 0, DateTime.UtcNow, "Jane");
}
