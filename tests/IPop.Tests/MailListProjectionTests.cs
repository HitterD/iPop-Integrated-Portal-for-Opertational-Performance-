using System.Data.Common;
using System.Text.RegularExpressions;
using IPop.Application.Mail;
using IPop.Infrastructure.Mail;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace IPop.Tests;

/// A mail list row shows a 120-character snippet. The list queries used to Include the whole Mail entity, so every
/// page of 20-100 rows pulled each full body (up to megabytes of HTML) from PostgreSQL only to throw all but 120
/// characters away. The body may be read only through a bounded substring.
public class MailListProjectionTests
{
    private sealed class Capture : DbCommandInterceptor
    {
        public readonly List<string> Sql = [];
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r)
        { Sql.Add(c.CommandText); return r; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e,
            InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { Sql.Add(c.CommandText); return ValueTask.FromResult(r); }
    }

    private static IPopDbContext Spy(TestDb t, Capture cap) =>
        new(new DbContextOptionsBuilder<IPopDbContext>(t.Options).AddInterceptors(cap).Options);

    /// What reaches the client is the OUTERMOST select list. EF wraps a paged query in a subquery that names every column
    /// (so the inner list always mentions Body), but a subquery's unused columns are never sent: only the outer list counts.
    /// There the body may appear only inside a bounded substring, never as a bare column.
    private static void AssertBodyOnlyBounded(IEnumerable<string> statements)
    {
        foreach (var sql in statements.Where(s => s.Contains("\"Mails\"") && s.Contains("\"Body\"")))
        {
            var from = sql.IndexOf("\nFROM", StringComparison.Ordinal);
            var outer = from < 0 ? sql : sql[..from];
            foreach (Match m in Regex.Matches(outer, @"""[A-Za-z0-9]+""\.""Body"""))
            {
                var before = outer[Math.Max(0, m.Index - 12)..m.Index];
                Assert.True(Regex.IsMatch(before, @"(substr|substring)\s*\($", RegexOptions.IgnoreCase),
                    "the full Body column is selected unbounded:\n" + sql);
            }
        }
    }

    private static async Task<(TestDb T, Guid Reader, Guid MailId)> SeedAsync(string body)
    {
        var t = new TestDb();
        var a = t.AddUser("MLP1"); var b = t.AddUser("MLP2");
        var sent = await new MailService(t.Verify()).SendAsync(a.Id, new ComposeMailRequest("Subject", body, [b.Nik], [], []), default);
        Assert.True(sent.Success, sent.Error);
        return (t, b.Id, sent.MailId!.Value);
    }

    [Fact]
    public async Task Folder_list_reads_only_a_bounded_prefix_of_each_body_and_keeps_the_snippet()
    {
        var huge = "<p>" + string.Join(" ", Enumerable.Range(0, 20_000).Select(i => $"word{i}")) + "</p>";
        var (t, reader, _) = await SeedAsync(huge);
        using var _t = t;
        var cap = new Capture();
        using var spy = Spy(t, cap);

        var page = await new MailService(spy).GetFolderAsync(reader, MailFolder.Inbox, 1, 20, null, default);

        var row = Assert.Single(page.Items);
        Assert.StartsWith("word0 word1 word2", row.Snippet);
        Assert.True(row.Snippet.Length <= 123, "snippet stays 120 characters plus the ellipsis");
        AssertBodyOnlyBounded(cap.Sql);
    }

    [Fact]
    public async Task Folder_list_snippet_is_identical_for_ordinary_html_bodies()
    {
        var (t, reader, _) = await SeedAsync("<div>Hello <b>team</b>,&nbsp;the&amp;report is <a href=\"https://x.test/very/long/path?q=1\">ready</a>.</div><p>Thanks</p>");
        using var _t = t;

        var row = Assert.Single((await new MailService(t.Verify()).GetFolderAsync(reader, MailFolder.Inbox, 1, 20, null, default)).Items);

        Assert.Equal("Hello team , the&report is ready . Thanks", row.Snippet);
    }

    [Fact]
    public async Task Thread_list_reads_only_a_bounded_prefix_of_each_body()
    {
        var huge = "<p>" + new string('x', 300_000) + "</p>";
        var (t, reader, mailId) = await SeedAsync(huge);
        using var _t = t;
        var cap = new Capture();
        using var spy = Spy(t, cap);

        var page = await new MailService(spy).GetThreadAsync(reader, mailId, 1, 50, default);

        Assert.Single(page.Items);
        AssertBodyOnlyBounded(cap.Sql);
    }
}
