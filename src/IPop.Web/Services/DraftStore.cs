using Microsoft.JSInterop;
using IPop.Application.Chat.Mentions;

namespace IPop.Web.Services;

public sealed class DraftStore(IJSRuntime js)
{
    public const int MaxDrafts = 50;
    public const string KeyPrefix = "ipop.chat.draft.";
    public const string IndexKey = "ipop.chat.drafts";

    public async Task<string?> LoadAsync(Guid roomId)
    {
        try { return await js.InvokeAsync<string?>("ipop.draftGet", roomId.ToString("D")); } catch { return null; }
    }

    public async Task SaveAsync(Guid roomId, string content)
    {
        var trimmed = content.Trim();
        if (string.IsNullOrEmpty(trimmed)) { await RemoveAsync(roomId); return; }
        if (trimmed.Length > 4000) trimmed = TrimToLimit(trimmed, 4000);
        try { await js.InvokeVoidAsync("ipop.draftSet", roomId.ToString("D"), trimmed, DateTime.UtcNow.ToString("O")); } catch { }
        await PruneAsync();
    }

    /// ipop-48c: LRU eviction — ipop.js:690 already maintains a roomId->ISO-timestamp
    /// index on every draftSet/draftRemove (draftList exposes it); it was written for
    /// this and never wired. Keeps the MaxDrafts=50 cap from being documentation-only,
    /// and bounds localStorage before the silent-quota-failure mode can trigger.
    public async Task PruneAsync()
    {
        try
        {
            var index = await js.InvokeAsync<Dictionary<string, string>>("ipop.draftList");
            if (index is null || index.Count <= MaxDrafts) return;
            foreach (var roomIdStr in index
                .OrderByDescending(kv => kv.Value)
                .Skip(MaxDrafts)
                .Select(kv => kv.Key))
            {
                // index keys are the "D"-format Guid strings RemoveAsync writes; parse back
                if (Guid.TryParseExact(roomIdStr, "D", out var staleRoomId))
                    await RemoveAsync(staleRoomId);
            }
        }
        catch { /* eviction is opportunistic — never fail a save because pruning hiccupped */ }
    }

    public async Task RemoveAsync(Guid roomId)
    {
        try { await js.InvokeVoidAsync("ipop.draftRemove", roomId.ToString("D")); } catch { }
    }

    public static string TrimToLimit(string content, int limit = 4000)
    {
        if (content.Length <= limit) return content;
        var cut = limit;
        // Avoid cutting inside a Mention_Token: find the nearest token start before cut
        var segs = MentionParser.Parse(content);
        int pos = 0;
        foreach (var seg in segs)
        {
            var raw = seg is MentionSegment.Text t ? t.Value : MentionPrinter.PrintToken(seg);
            if (pos + raw.Length > cut)
            {
                // cut falls inside this segment — drop it entirely if it's a token, else truncate
                if (seg is not MentionSegment.Text)
                    cut = pos;
                break;
            }
            pos += raw.Length;
        }
        return content[..Math.Min(cut, content.Length)];
    }
}
