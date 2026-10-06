using IPop.Application.Chat;

namespace IPop.Web.Services;

/// Why a chat read did not produce data. Kept apart from the data itself so the page can tell
/// "still loading", "really empty" and "failed" from one another instead of showing a skeleton
/// forever or a false "No conversations yet".
public enum ChatLoadError
{
    None,
    /// Network, timeout, 5xx or an unexpected answer: worth a Retry.
    Unavailable,
    /// 403/404: the room was deleted or the user is no longer a member. Retrying will not help.
    Gone,
}

// Room-list / room-open / history error state and the retry entry points. The fetches
// themselves live in ChatState.cs (Room Operations); this part owns the bookkeeping.
public partial class ChatState
{
    /// The last room-list fetch (initial, search, or a further page) failed. Lists already on
    /// screen are kept; when nothing is on screen the page shows this instead of a skeleton.
    public ChatLoadError RoomsError { get; private set; }

    /// Opening (or refreshing) the active room failed. Unavailable with history on screen is
    /// a banner; Unavailable with nothing on screen replaces the skeleton; Gone means the room
    /// details/history were dropped and the pane should say the conversation is unavailable.
    public ChatLoadError RoomError { get; private set; }

    /// The last "load older messages" request failed. The paging cursor did not move, so
    /// RetryOlderMessagesAsync asks for the same page again.
    public ChatLoadError OlderError { get; private set; }

    /// Bumped whenever the active room changes or its load restarts. A response only lands if
    /// the generation it started under is still current, so a slow answer for room A (or for a
    /// superseded attempt at the same room) can never paint over room B.
    private int _roomGen;

    /// Same idea for the room lists: the newest LoadRooms/Search call wins.
    private int _roomsGen;
    /// Separate from _roomsGen: a search and a full-list load no longer write the same lists.
    private int _searchGen;

    public async Task RetryRoomsAsync()
    {
        await LoadRoomsAsync();
        if (RoomSearchResults is not null && _roomSearch.Length > 0) await SearchRoomsAsync(_roomSearch);
    }

    /// Re-runs the active room's details + page 1. OpenRoomAsync returns early for the room that
    /// is already open, so retry has its own entry point with a fresh CancellationTokenSource.
    /// History already on screen stays put while the retry runs.
    public async Task RetryActiveRoomAsync()
    {
        if (ActiveRoomId is not { } roomId || roomId == _openingRoomId) return;
        _openingRoomId = roomId;
        _openCts?.Cancel();
        _openCts?.Dispose();
        var cts = new CancellationTokenSource();
        _openCts = cts;
        ResetRoomLoadState();
        var gen = _roomGen;
        IsLoadingMessages = ActiveMessages.Count == 0;
        StateChanged?.Invoke();
        await LoadOpenedRoomAsync(roomId, cts, gen);
    }

    public Task RetryOlderMessagesAsync()
    {
        if (OlderError == ChatLoadError.None) return LoadOlderMessagesAsync();
        OlderError = ChatLoadError.None;
        StateChanged?.Invoke();
        return LoadOlderMessagesAsync();
    }

    private void ResetRoomLoadState()
    {
        _roomGen++;
        _detailsFailed = _historyFailed = false;
        RoomError = ChatLoadError.None;
        OlderError = ChatLoadError.None;
    }

    // The room's details and its page 1 load independently (in parallel on open, and one at a
    // time from hub events / reconnect). Each records its own last outcome, so one side
    // succeeding never hides the other side's failure; RoomError is Unavailable while either
    // is failed. Gone overrides both and is only cleared by opening or retrying.
    private bool _detailsFailed, _historyFailed;

    private void SetPartFailed(bool details, bool failed)
    {
        if (details) _detailsFailed = failed; else _historyFailed = failed;
        if (RoomError == ChatLoadError.Gone) return;
        RoomError = _detailsFailed || _historyFailed ? ChatLoadError.Unavailable : ChatLoadError.None;
    }

    internal static ChatLoadError Classify(GetStatus status) => status switch
    {
        GetStatus.Ok => ChatLoadError.None,
        GetStatus.Forbidden or GetStatus.NotFound => ChatLoadError.Gone,
        _ => ChatLoadError.Unavailable,
    };

    /// Raised once a room is confirmed gone (403/404), after ChatState dropped its own caches.
    /// Owners of other per-room state (outbox / pending-send snapshot, composer text) clear
    /// theirs here so nothing for a room the user may no longer see survives in memory/storage.
    public event Action<Guid>? RoomGone;

    /// The server says this user cannot see the room (deleted, removed, never a member).
    /// Drop every cached trace of it so nothing unauthorized is painted from memory, take it
    /// out of the lists, and (if it is the open room) blank the pane behind a Gone state.
    /// `definitive` = the answer came from the room endpoint, which is the membership check;
    /// only then is the stored draft removed too (same as removedFromRoom).
    private void HandleRoomGone(Guid roomId, bool definitive)
    {
        ForgetRoom(roomId);
        if (definitive && _drafts is not null) _ = _drafts.RemoveAsync(roomId); // swallows its own failures
        var listChanged = DirectRooms.RemoveAll(r => r.Id == roomId) > 0;
        listChanged |= GroupRooms.RemoveAll(r => r.Id == roomId) > 0;
        listChanged |= ArchivedRooms.RemoveAll(r => r.Id == roomId) > 0;
        _mentionFlags.Remove(roomId);
        if (roomId == ActiveRoomId)
        {
            _roomGen++;
            _openCts?.Cancel();
            ActiveRoom = null;
            ActiveMessages = [];
            MessagePage = 1;
            HasOlderMessages = false;
            IsLoadingMessages = false;
            RoomError = ChatLoadError.Gone;
            OlderError = ChatLoadError.None;
            _fresh.Clear();
            foreach (var t in _typingCts.Values) { t.Cancel(); t.Dispose(); }
            _typingCts.Clear();
            TypingUsers.Clear();
        }
        if (listChanged) RoomListChanged?.Invoke();
        if (definitive) RoomGone?.Invoke(roomId);
        StateChanged?.Invoke();
    }

    private bool IsCurrent(Guid roomId, int gen) => gen == _roomGen && roomId == ActiveRoomId;

    private string MessagesUrl(Guid roomId, int page) => $"/api/chat/rooms/{roomId}/messages?page={page}&pageSize=50";
}
