using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

public class ChatUploadPolicyTests
{
    [Fact]
    public void All_paths_share_five_file_and_fifty_megabyte_limits()
    {
        var files = Enumerable.Range(0, 7).Select(i => new ChatUploadPolicy.File($"{i}.txt", 20)).ToList();
        var plan = ChatUploadPolicy.Plan(files, 3);
        Assert.Equal([0, 1], plan.Accepted);
        Assert.Contains("5 files", plan.Notice);
        Assert.Contains("5 file(s) were not added", plan.Notice);
    }

    [Fact]
    public void Invalid_files_do_not_consume_remaining_slots()
    {
        var plan = ChatUploadPolicy.Plan([
            new("empty.txt", 0), new("large.mp4", ChatUploadPolicy.MaxBytes + 1),
            new("ok.mp4", ChatUploadPolicy.MaxBytes), new("ok.txt", 1)
        ], 3);
        Assert.Equal([2, 3], plan.Accepted);
        Assert.Contains("empty.txt", plan.Notice);
        Assert.Contains("large.mp4", plan.Notice);
        Assert.DoesNotContain("not added", plan.Notice);
    }

    [Fact]
    public void Full_message_accepts_no_more_files_and_explains_why()
    {
        var plan = ChatUploadPolicy.Plan([new("more.txt", 1)], 5);
        Assert.Empty(plan.Accepted);
        Assert.Contains("5 files", plan.Notice);
    }

    [Fact]
    public void Valid_selection_has_no_error()
    {
        var plan = ChatUploadPolicy.Plan([new("one.txt", 1)], 0);
        Assert.Single(plan.Accepted);
        Assert.Null(plan.Notice);
    }
}
