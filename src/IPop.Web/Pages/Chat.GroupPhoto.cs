using System.Net.Http.Json;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

/// Group photo in Group info / header / list: click opens the photo viewer; owners and admins get
/// "Change photo" there (and on the hero when the group has no photo yet). Same upload endpoint and
/// limits as Group settings (2 MB, png/jpeg/webp/gif).
public partial class Chat
{
    private InputFile? groupPhotoInput;
    private bool groupPhotoUploading;
    private const long GroupPhotoMaxBytes = 2 * 1024 * 1024;

    /// Header / list / hero avatar click on a group: photo → viewer; no photo → Group info.
    private void OpenGroupPhoto()
    {
        var room = ChatSvc.ActiveRoom;
        if (room?.Type != "group") return;
        if (room.AvatarUrl is null) { if (CanManageMembers) _ = PickGroupPhoto(); return; }
        PhotoViewer.OpenUrl(PhotoUrl(room.AvatarUrl), GetActiveRoomName(),
            CanManageMembers ? "Change photo" : null, CanManageMembers ? PickGroupPhoto : null);
    }

    /// Header avatar: a group with a photo opens the viewer, anything else opens Group info.
    private void HeaderAvatarClick()
    {
        if (ChatSvc.ActiveRoom is { Type: "group", AvatarUrl: not null }) OpenGroupPhoto();
        else ToggleDetailPanel();
    }

    private async Task PickGroupPhoto()
    {
        if (!CanManageMembers || groupPhotoUploading) return;
        if (groupPhotoInput?.Element is { } el)
            try { await JS.InvokeVoidAsync("ipop.clickFileInput", el); } catch { }
    }

    private async Task OnGroupPhotoSelected(InputFileChangeEventArgs e)
    {
        var roomId = ChatSvc.ActiveRoomId;
        var file = e.File;
        if (roomId is null || file is null) return;
        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) { Toasts.Show("Choose an image file.", ToastKind.Warning); return; }
        if (file.Size > GroupPhotoMaxBytes) { Toasts.Show("Photo must be 2 MB or less.", ToastKind.Warning); return; }
        groupPhotoUploading = true;
        StateHasChanged();
        try
        {
            using var ms = new MemoryStream();
            await using (var s = file.OpenReadStream(GroupPhotoMaxBytes)) await s.CopyToAsync(ms);
            using var content = new MultipartFormDataContent();
            var part = new ByteArrayContent(ms.ToArray());
            part.Headers.ContentType = new(file.ContentType);
            content.Add(part, "file", file.Name);
            var res = await Auth.SendMultipartAsync($"/api/chat/rooms/{roomId}/avatar", content);
            if (!res.IsSuccessStatusCode)
            {
                var code = await GroupPhotoErrorAsync(res);
                Toasts.Show(code, ToastKind.Error);
                return;
            }
            await ChatSvc.LoadRoomsAsync();
            await ChatSvc.LoadActiveRoomAsync();
            Toasts.Show("Group photo updated.", ToastKind.Success);
        }
        catch (Exception ex) { Toasts.Show("Upload failed: " + ex.Message, ToastKind.Error); }
        finally { groupPhotoUploading = false; StateHasChanged(); }
    }

    private static async Task<string> GroupPhotoErrorAsync(HttpResponseMessage res)
    {
        try
        {
            var b = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            return b?.GetValueOrDefault("code")?.ToString() switch
            {
                "too_large" => "Photo must be 2 MB or less.",
                "not_an_image" or "invalid_image" => "That file is not a supported image.",
                "forbidden" => "Only the group owner or admins can change the photo.",
                _ => $"Could not change the photo ({(int)res.StatusCode}).",
            };
        }
        catch { return $"Could not change the photo ({(int)res.StatusCode})."; }
    }
}
