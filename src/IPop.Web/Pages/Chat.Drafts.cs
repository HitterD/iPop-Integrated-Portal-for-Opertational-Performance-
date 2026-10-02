// Chat page — composer draft lifecycle (DraftStore + DraftSendLedger).
//
// Part of the Chat partial class. The parent wires these in (see the call sites listed on
// each member); nothing here edits another part's fields beyond the composer state it owns
// during a restore.
//
// Flow:
//   input/mention change → StageComposerDraft()      (sync; JS writes after ~400 ms)
//   send handed to ChatState → OnComposerSendStarted  (capture the draft version it consumed)
//   SendSettled Confirmed (same room + key) → RemoveIfMatches (newer typing survives)
//   SendSettled Failed/Unknown → draft stays, so a reload/navigation still has the text
//   RoomGone / sign-out → draft + snapshots dropped; Dispose can't resurrect them
using IPop.Web.Models;
using IPop.Web.Services;

namespace IPop.Web.Pages;

public partial class Chat
{
    private readonly DraftSendLedger draftSends = new();
    private bool draftsHooked;
    /// Set when the room or account went away: the composer's text must not be written back.
    private Guid? draftSuppressedRoom;
    private bool draftSignedOut;

    /// Call once from OnInitializedAsync.
    private void InitializeDraftLifecycle()
    {
        if (draftsHooked) return;
        draftsHooked = true;
        ChatSvc.SendSettled += OnDraftSendSettled;
        ChatSvc.RoomGone += OnDraftRoomGone;
        Auth.SignedOut += OnDraftSignedOut;
    }

    /// Call from Dispose() (before clearing composer state).
    private void DisposeDraftLifecycle()
    {
        if (!draftsHooked) return;
        FlushComposerDraft();
        draftsHooked = false;
        ChatSvc.SendSettled -= OnDraftSendSettled;
        ChatSvc.RoomGone -= OnDraftRoomGone;
        Auth.SignedOut -= OnDraftSignedOut;
    }

    /// Call after every composer text / picked-mention change (oninput @bind:after, mention pick).
    /// `roomId` defaults to the open room; pass the previous room when switching away.
    private void StageComposerDraft(Guid? roomId = null)
    {
        if ((roomId ?? openedRoomId) is not { } room || !CanWriteDraft(room)) return;
        var wire = ComposerWireText();
        // Empty composer while a send of this room still owns the stored text: leave it, the
        // confirmation (RemoveIfMatches) decides. Otherwise an empty composer removes it.
        if (string.IsNullOrWhiteSpace(wire) && draftSends.HoldsDraft(room, Drafts)) return;
        Drafts.Stage(room, wire);
    }

    /// Stages the current composer and writes everything now (room switch, close, dispose).
    private void FlushComposerDraft(Guid? roomId = null)
    {
        StageComposerDraft(roomId);
        Drafts.Flush();
    }

    /// The stored draft for a room, unless a send of that very text is still in flight/failed
    /// (its bubble already shows it) or another room was opened meanwhile. Use instead of
    /// LoadAsync when opening a room.
    private async Task<string?> SafeLoadDraftAsync(Guid roomId)
    {
        if (draftSuppressedRoom == roomId) draftSuppressedRoom = null; // reopened: membership checked again
        if (draftSends.HoldsDraft(roomId, Drafts)) return null;
        var text = await Drafts.LoadAsync(roomId);
        if (openedRoomId != roomId || draftSends.HoldsDraft(roomId, Drafts)) return null;
        return text;
    }

    private bool CanWriteDraft(Guid room) => !draftSignedOut && draftSuppressedRoom != room && Auth.Me is not null;

    partial void OnComposerSendStarted(ComposerSendSnapshot snapshot)
    {
        // Make sure the stored draft is exactly what is being sent, then remember its version.
        if (!CanWriteDraft(snapshot.RoomId)) return;
        Drafts.Stage(snapshot.RoomId, ComposerMentionModel.ToWire(snapshot.ComposerText,
            ComposerMentionModel.Prune(snapshot.ComposerText, pickedMentions)));
        Drafts.Flush();
        draftSends.Started(snapshot.RoomId, snapshot.IdempotencyKey, Drafts.CaptureSnapshot(snapshot.RoomId), snapshot.Attachments);
    }

    private void OnDraftSendSettled(SendOutcome o)
    {
        var result = draftSends.Settled(o, Drafts);
        if (result == DraftSettleResult.KeptUnknown && !_disposed)
            Toasts.Show("Not confirmed — the text is kept as a draft. Use Retry on the message (it won't send twice) rather than sending it again.", ToastKind.Warning);
    }

    private void OnDraftRoomGone(Guid roomId)
    {
        draftSends.ForgetRoom(roomId);
        _ = Drafts.RemoveAsync(roomId);
        if (openedRoomId != roomId) return;
        draftSuppressedRoom = roomId;
        ClearComposerForDrafts();
    }

    private void OnDraftSignedOut()
    {
        draftSignedOut = true;
        draftSends.Clear();
        ClearComposerForDrafts();
    }

    private void ClearComposerForDrafts()
    {
        messageText = "";
        pickedMentions = [];
        replyToMsg = null;
        ClearPendingPreviews();
        pendingAttachments = [];
        if (!_disposed) _ = InvokeAsync(StateHasChanged);
    }

    /// Bubble "Edit"/"Restore" on a failed message: removes the bubble and puts its text,
    /// reply and attachments back into the composer. Refuses (returns false) when the composer
    /// already has content or the message belongs to another room.
    private void RestoreFailedMessage(Guid tempId)
    {
        if (!RestoreFailedToComposer(tempId)) Toasts.Show("Clear the composer first.", ToastKind.Info);
    }

    private bool RestoreFailedToComposer(Guid tempId)
    {
        if (!string.IsNullOrWhiteSpace(messageText) || pendingAttachments.Count > 0) return false;
        var room = ChatSvc.ActiveRoomId;
        if (room is null || openedRoomId != room) return false;
        if (!ChatSvc.IsFailed(tempId)) return false;
        var recorded = draftSends.Take(tempId);
        var unsent = ChatSvc.DiscardFailed(tempId);
        if (unsent is null) return false;
        if (unsent.RoomId != room) return false;

        LoadComposerFromWire(unsent.Content);
        replyToMsg = unsent.ReplyToId is { } rid ? ChatSvc.ActiveMessages.FirstOrDefault(m => m.Id == rid) : null;
        // The recorded items carry the local blob previews (still alive: only a confirmed
        // send revokes them); they are owned by pendingAttachments again from here.
        pendingAttachments = recorded is { Count: > 0 }
            ? recorded.ToList()
            : unsent.Attachments.Select(a => new PendingAttachmentItem(a.Id, a.FileName, a.FileSize, a.MimeType, null)).ToList();
        draftSuppressedRoom = null;
        StageComposerDraft();
        focusComposerPending = true;
        return true;
    }
}
