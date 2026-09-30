using System.Text.Json;
using System.Text.Json.Nodes;
using IPop.Application.Chat;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed partial class ChatService
{
    private const string PollPrefix = "[poll]";

    // Polls live in the message content as "[poll]{json}" with Votes: optionIndex -> [nik].
    // Any active member may vote; the write is a compare-and-swap on Content so two voters
    // racing never drop each other's vote (the loser re-reads and re-applies).
    public async Task<MessageResultDto> VotePollAsync(Guid userId, Guid messageId, int optionIndex, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var message = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct);
            if (message is null || message.IsDeleted) return new MessageResultDto(false, null, "message_not_found");
            var isMember = await db.ChatRoomMembers.AsNoTracking()
                .AnyAsync(m => m.RoomId == message.RoomId && m.UserId == userId && m.LeftAt == null, ct);
            if (!isMember) return new MessageResultDto(false, null, "not a member");
            var nik = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
            if (nik is null) return new MessageResultDto(false, null, "not a member");

            var original = message.Content;
            if (!original.StartsWith(PollPrefix, StringComparison.Ordinal)) return new MessageResultDto(false, null, "not_a_poll");
            JsonObject poll;
            try { poll = JsonNode.Parse(original[PollPrefix.Length..]) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { return new MessageResultDto(false, null, "not_a_poll"); }

            var optionCount = (poll["Options"] as JsonArray)?.Count ?? 0;
            if (optionIndex < 0 || optionIndex >= optionCount) return new MessageResultDto(false, null, "invalid_option");
            var multiple = poll["AllowMultiple"]?.GetValue<bool>() ?? false;

            var votes = new Dictionary<string, List<string>>();
            if (poll["Votes"] is JsonObject existing)
                foreach (var (key, value) in existing)
                    votes[key] = (value as JsonArray)?.Select(v => v?.GetValue<string>()).OfType<string>().Distinct().ToList() ?? [];

            var target = optionIndex.ToString();
            var hadVote = votes.TryGetValue(target, out var list) && list.Contains(nik);
            if (!multiple)
                foreach (var v in votes.Values) v.Remove(nik);
            if (hadVote) votes[target].Remove(nik);
            else
            {
                if (!votes.ContainsKey(target)) votes[target] = [];
                votes[target].Add(nik);
            }
            poll["Votes"] = JsonSerializer.SerializeToNode(votes.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value));

            var updated = PollPrefix + poll.ToJsonString();
            var rows = await db.ChatMessages
                .Where(m => m.Id == messageId && m.Content == original)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Content, updated), ct);
            if (rows == 0) continue; // someone else voted in between — retry on fresh content

            db.ChangeTracker.Clear();
            var fresh = await db.ChatMessages
                .Include(m => m.Sender).Include(m => m.Attachments).Include(m => m.Reactions).Include(m => m.ReadReceipts)
                .AsNoTracking().FirstAsync(m => m.Id == messageId, ct);
            return new MessageResultDto(true, MapToMessageDto(fresh, userId), null);
        }
        return new MessageResultDto(false, null, "conflict");
    }
}
