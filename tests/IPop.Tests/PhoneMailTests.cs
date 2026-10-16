using Bunit;
using IPop.Web.Components.Phone.Mail;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Wave 2 phone mail (mockup v1 c/d, v2 M1–M2). The page keeps all state; these are presentational.
public class PhoneMailTests
{
    private static BunitContext Ctx()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(new OverlayStack());
        return ctx;
    }

    private static readonly IReadOnlyList<MailboxItem> Boxes =
    [
        new("inbox", "Inbox", "inbox", 12, true, true),
        new("starred", "Starred", "star", 5, false, false),
        new("draft", "Drafts", "file-text", 2, false, false),
        new("sent", "Sent", "send", 0, false, false),
    ];

    private static readonly IReadOnlyList<MailLabel> Labels =
    [
        new(Guid.NewGuid(), "ICT", "#0a84ff", 48),
        new(Guid.NewGuid(), "HC", "#ff9f0a", 0),
    ];

    [Fact]
    public void Mailboxes_sheet_lists_folders_with_counts_then_labels_then_settings()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<MailboxesSheet>(p => p.Add(x => x.Open, true).Add(x => x.Boxes, Boxes).Add(x => x.Labels, Labels));

        Assert.Equal("Mailboxes", cut.Find(".ph-sheet-title").TextContent);
        var rows = cut.FindAll(".m-mail-box .ph-row");
        Assert.Equal(["Inbox", "Starred", "Drafts", "Sent"], rows.Select(r => r.QuerySelector(".ph-row-title")!.TextContent).ToList());
        Assert.Equal("12", rows[0].QuerySelector(".m-mail-cnt.acc")!.TextContent);
        Assert.Equal("page", rows[0].GetAttribute("aria-current"));
        Assert.Null(rows[3].QuerySelector(".m-mail-cnt"));

        var labels = cut.FindAll(".m-mail-lbls .ph-row");
        Assert.Equal(2, labels.Count);
        Assert.Contains("ICT", labels[0].TextContent);
        Assert.Contains("48", labels[0].TextContent);
        Assert.Contains("--c:#0a84ff", labels[0].QuerySelector(".m-mail-lchip")!.GetAttribute("style"));

        Assert.Contains("Rules & filters", cut.Find(".m-mail-more").TextContent);
        Assert.Contains("Mail settings", cut.Find(".m-mail-more").TextContent);
    }

    [Fact]
    public void Mailboxes_sheet_fires_folder_label_and_settings_callbacks()
    {
        using var ctx = Ctx();
        string? box = null, label = null; var settings = new List<string>(); var closed = 0;
        var cut = ctx.Render<MailboxesSheet>(p => p.Add(x => x.Open, true).Add(x => x.Boxes, Boxes).Add(x => x.Labels, Labels)
            .Add(x => x.OnBox, (string b) => box = b).Add(x => x.OnLabel, (string l) => label = l)
            .Add(x => x.OnSettings, (string s) => settings.Add(s)).Add(x => x.OnClose, () => closed++));

        cut.FindAll(".m-mail-box .ph-row")[2].Click();
        Assert.Equal("draft", box);
        cut.FindAll(".m-mail-lbls .ph-row")[1].Click();
        Assert.Equal("HC", label);
        cut.Find(".m-mail-lbls-h button[aria-label='New label']").Click();
        foreach (var b in cut.FindAll(".m-mail-more .ph-row")) b.Click();
        Assert.Equal(["labels", "rules", "signature"], settings);
        cut.Find(".ph-sheet-trail button").Click();
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Mailboxes_sheet_shows_no_labels_note_and_nothing_when_closed()
    {
        using var ctx = Ctx();
        var cut = ctx.Render<MailboxesSheet>(p => p.Add(x => x.Open, true).Add(x => x.Boxes, Boxes).Add(x => x.Labels, []));
        Assert.Contains("No labels yet", cut.Markup);
        cut.Render(p => p.Add(x => x.Open, false));
        Assert.Empty(cut.FindAll(".ph-sheet"));
    }
}
