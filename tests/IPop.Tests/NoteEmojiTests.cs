using IPop.Application.Notes;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// A page icon is cut to the length limit between whole characters: half an emoji used to be stored as "??".
public class NoteEmojiTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData("🧩", "🧩")]
    [InlineData("👋🏽", "👋🏽")]                       // skin tone: one character of 4 UTF-16 units
    [InlineData("👨‍👩‍👧‍👦", "👨‍👩‍👧‍👦")]                // family: 11 units, still within 16
    [InlineData("a🧩🧩🧩🧩🧩🧩🧩🧩", "a🧩🧩🧩🧩🧩🧩🧩")]   // 17 units: a plain cut at 16 halves the last emoji; it is dropped whole
    [InlineData("👨‍👩‍👧‍👦👨‍👩‍👧‍👦", "👨‍👩‍👧‍👦")]              // 22 units: the second family does not fit, none of it is kept
    public async Task Page_icon_is_never_cut_in_the_middle_of_an_emoji(string input, string stored)
    {
        using var t = new TestDb();
        var a = t.AddUser("a");
        var r = await new NoteService(t.Verify()).CreateAsync(a.Id, new CreateNoteRequest(null, "Page", input, null, null, null), Ct);
        Assert.True(r.Success, r.Error);
        var emoji = await t.Verify().NotePages.Where(p => p.Id == r.Data!.Id).Select(p => p.Emoji).FirstAsync();
        Assert.Equal(stored, emoji);
        Assert.DoesNotContain('�', emoji!);
        Assert.False(char.IsHighSurrogate(emoji![^1]), "no lone high surrogate at the end");
    }
}
