using System.Text.RegularExpressions;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// Bugs found by the full Mail QA on a copy DB (2026-10-14, .scratch/mail-qa2/REPORT.md).
public class MailQa2Tests
{
    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "IPop.slnx"))) root = root.Parent;
        return root!.FullName;
    }

    private static string Css() => File.ReadAllText(Path.Combine(Root(), "src/IPop.Web/wwwroot/css/v6/mail-receipts.css"));

    private static string Rule(string css, string selector)
        => Regex.Match(css, Regex.Escape(selector) + @"\s*\{([^}]*)\}").Groups[1].Value;

    // The 540 px Recipients panel was anchored to the recipients line (left ≈ 76 px into a 486 px reading card at
    // 1440) and the card's overflow clipped its right half. It must anchor to the whole sender block and stretch
    // across it instead of a fixed width.
    [Fact]
    public void Recipients_panel_anchors_to_the_sender_block_and_fits_the_reading_card()
    {
        var css = Css();
        Assert.Matches(@"position:\s*relative", Rule(css, ".mail-page .detail-sender-block"));
        Assert.Matches(@"position:\s*static", Rule(css, ".mail-page .detail-sender-block .mrh"));
        var panel = Rule(css, ".mail-page .detail-sender-block .mrr-pop.mrh-panel");
        Assert.Matches(@"left:\s*12px", panel);
        Assert.Matches(@"right:\s*12px", panel);
        Assert.Matches(@"width:\s*auto", panel);
    }

    // On a 390 px phone the thread action row (Quick reply · Reply · Reply all · Forward) ran past the card edge.
    [Fact]
    public void Thread_actions_wrap_instead_of_running_off_a_phone()
        => Assert.Matches(@"flex-wrap:\s*wrap", Rule(Css(), ".mail-page .thr-acts"));

    // EPop mail with five archive chips: the collapsed To line squeezed "you" down to "y." while chips kept their width.
    [Fact]
    public void Names_on_the_recipients_line_never_shrink_below_you()
        => Assert.Matches(@"min-width:\s*3\.5em", Rule(Css(), ".mrh-line > .mrh-names"));

    // Inbox "Starred" tab showed the all-folder starred total (2) while listing only the inbox ones (1).
    [Fact]
    public void Inbox_starred_tab_counts_inbox_starred_mail()
    {
        var page = File.ReadAllText(Path.Combine(Root(), "src/IPop.Web/Pages/Mail.razor"));
        Assert.Matches(@"StarredCount\s*=>\s*activeFolder\s*==\s*MailFolder\.Inbox\s*\?\s*\(counts\?\.InboxStarred", page);
    }
}

/// Server side of the Mail QA 2 bugs.
public class MailQa2ServiceTests
{
    private static async Task<Guid> Send(IPop.Infrastructure.Mail.MailService svc, IPop.Core.Entities.User from, IReadOnlyList<string> to, int? confirmed = null)
    {
        var r = await svc.SendAsync(from.Id, new IPop.Application.Mail.ComposeMailRequest("Hi", "<p>x</p>", to, [], [], ConfirmedRecipientCount: confirmed), default);
        Assert.True(r.Success, r.Error);
        return r.MailId!.Value;
    }

    // EPop imports store the original address-book entries as {"kind":"epop","id":Guid.Empty} chips. Receipts treated
    // them as shared DirGroups (looked up by Guid.Empty) and listed five "groups" with 0 of 0 read.
    [Fact]
    public async Task Epop_archive_chips_are_not_receipt_groups()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var svc = new IPop.Infrastructure.Mail.MailService(t.Verify());
        var id = await Send(svc, a, [b.Nik]);
        var mail = t.Db.Mails.Single(m => m.Id == id);
        mail.RecipientGroupsJson = """[{"field":"to","kind":"epop","id":"00000000-0000-0000-0000-000000000000","name":"SJA JAKARTA","count":1}]""";
        t.Db.SaveChanges();

        var r = await new IPop.Infrastructure.Mail.MailService(t.Verify()).GetReceiptsAsync(b.Id, id, default);
        Assert.Empty(r!.Groups);
    }

    // Mass mail detail caps names at 50; a recipient past the cap (e.g. the 56th) was missing from Recipients, so the
    // header said "To 55 others" instead of "you and 55 others".
    [Fact]
    public async Task Mass_mail_detail_always_lists_the_reader()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var people = Enumerable.Range(0, 60).Select(i => t.AddUser($"P{i:D3}")).ToList();
        var svc = new IPop.Infrastructure.Mail.MailService(t.Verify());
        var id = await Send(svc, a, people.Select(p => p.Nik).ToList(), confirmed: 60);
        var last = people[^1];
        // Recipient rows are ordered by CreatedAt: make the reader's row the newest, past the 50-name cap.
        foreach (var row in t.Db.MailRecipients.Where(r => r.MailId == id)) row.CreatedAt = row.UserId == last.Id ? DateTime.UtcNow.AddMinutes(5) : DateTime.UtcNow.AddMinutes(-5);
        t.Db.SaveChanges();

        var d = await new IPop.Infrastructure.Mail.MailService(t.Verify()).GetAsync(last.Id, id, default);
        Assert.True(d!.Recipients.Count <= IPop.Application.Mail.MailLimits.DetailRecipients);
        Assert.Contains(d.Recipients, r => r.UserId == last.Id);
    }

    [Fact]
    public async Task Counts_report_inbox_starred_separately_from_all_starred()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var svc = new IPop.Infrastructure.Mail.MailService(t.Verify());
        var m1 = await Send(svc, a, [b.Nik]);
        var m2 = await Send(svc, a, [b.Nik]);
        await svc.ToggleStarAsync(b.Id, m1, default);
        await svc.ToggleStarAsync(b.Id, m2, default);
        await svc.MoveToFolderAsync(b.Id, m2, IPop.Application.Mail.MailFolder.Archive, default);

        var c = await new IPop.Infrastructure.Mail.MailService(t.Verify()).GetFolderCountsAsync(b.Id, default);
        Assert.Equal(2, c.StarredTotal);
        Assert.Equal(1, c.InboxStarred);
    }
}
