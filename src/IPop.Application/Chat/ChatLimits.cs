namespace IPop.Application.Chat;

/// <summary>Central numeric bounds for the Group Page Enhancement spec.</summary>
public static class ChatLimits
{
    /// <summary>Max thread replies per parent. Design resolves the 500 (chat-module) vs 255 (group-page-enhancement) conflict here.</summary>
    public const int ThreadReplies = 255;

    public const int MentionRecordsPerMessage = 255;
    public const int MentionTokensPerMessage = 50;
    public const int MessageContentLength = 4000;
    public const int PinnedRoomsPerUser = 5;
    public const int PinnedMessagesPerRoom = 50;
    public const int GroupMembers = 256;
    public const int MentionQueryLength = 40;
    public const int MentionPopupItems = 10;
}
