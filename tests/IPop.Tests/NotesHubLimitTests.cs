using Xunit;

namespace IPop.Tests;

/// The Notes hub must accept a full-size Yjs update (512 KB, base64 ~683 KB). With SignalR's 32 KB default every
/// update over ~24 KB (a pasted table) dropped the connection and the edit never saved.
public class NotesHubLimitTests
{
    [Fact]
    public void Notes_hub_receive_limit_covers_the_largest_allowed_update()
    {
        var program = File.ReadAllText(Path.Combine(NotifierLinkTests.RepoRoot(), "src", "IPop.Api", "Program.cs"));
        Assert.Contains("AddHubOptions<IPop.Api.Notes.NotesHub>(o => o.MaximumReceiveMessageSize = 768 * 1024)", program);
        Assert.True(768 * 1024 > (512 * 1024 + 2) / 3 * 4 + 256, "limit must fit a base64 512 KB update plus framing");
    }
}
