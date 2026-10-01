using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using IPop.Web.Services;
using Microsoft.JSInterop;

namespace IPop.Web.Components.Chat;

/// Directory row for the new-chat panel: position and department feed the second line
/// ("Packing Machine Operator · Production").
public sealed record NewChatPerson(Guid Id, string Nik, string Name, string? Email,
    string? Position, string? Department, string PresenceStatus);

/// What the page needs to create a group: name, members and an optional photo that is
/// uploaded right after the room exists (the avatar endpoint needs a room id).
public sealed record NewGroupDraft(string Name, IReadOnlyList<NewChatPerson> Members,
    byte[]? Photo, string? PhotoContentType, string? PhotoFileName);

public enum NewChatStep { Chat, Members, Details }

public partial class NewChatPanel
{
    /// The server needs at least 3 members including the creator; there is no client cap
    /// beyond that (the server enforces its own maximum).
    public const int MinGroupPicks = 2;
    private const long MaxPhotoBytes = 2 * 1024 * 1024;

    /// Open straight on the "Add group members" step (the "New group" entry points).
    [Parameter] public bool GroupMode { get; set; }
    /// People to preselect in group mode (Directory → "Create group"). Arrives async, so new
    /// entries are merged in as the list grows; people the user removed are not re-added.
    [Parameter] public IReadOnlyList<NewChatPerson>? Preselected { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    /// A person was clicked in the "New chat" step: open (or create) the DM.
    [Parameter] public EventCallback<NewChatPerson> OnStartDm { get; set; }
    /// Create the group. Returns true when it worked, so the panel can re-enable on failure.
    [Parameter] public Func<NewGroupDraft, Task<bool>>? OnCreateGroup { get; set; }

    private NewChatStep step;
    private string query = "";
    private readonly List<NewChatPerson> picked = [];
    private readonly HashSet<Guid> preselectSeen = [];
    private List<NewChatPerson> people = [];
    private bool loading = true;
    private List<NewChatPerson>? searchPeople;
    private string? searchFor;
    private CancellationTokenSource? searchCts;
    private bool searching;
    private Guid? startingDm;

    private string groupName = "";
    private byte[]? photoBytes;
    private string? photoType, photoName, photoPreview;
    private bool creating;
    private readonly string photoInputId = "nc-photo-" + Guid.NewGuid().ToString("N")[..8];

    private ElementReference searchRef, nameRef;
    private bool focusPending = true;
    private bool disposed;

    public NewChatStep Step => step;

    private string Title => step switch
    {
        NewChatStep.Members => "Add group members",
        NewChatStep.Details => "New group",
        _ => "New chat",
    };

    private bool CanCreate => !creating && picked.Count >= MinGroupPicks && !string.IsNullOrWhiteSpace(groupName);

    protected override void OnInitialized()
    {
        step = GroupMode ? NewChatStep.Members : NewChatStep.Chat;
        Auth.PresenceChanged += OnPresenceChanged;
        _ = LoadPeopleAsync();
    }

    protected override void OnParametersSet()
    {
        if (Preselected is null) return;
        foreach (var p in Preselected)
        {
            if (!preselectSeen.Add(p.Id)) continue;
            if (picked.All(m => m.Id != p.Id)) picked.Add(p);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try { await JS.InvokeVoidAsync("ipop.icons"); } catch { }
        if (!focusPending) return;
        focusPending = false;
        try { await (step == NewChatStep.Details ? nameRef : searchRef).FocusAsync(); } catch { }
    }

    /// The default (empty-query) list. The endpoint caps it at 300 names, so 2+ characters
    /// search the server instead (OnSearchChanged).
    private async Task LoadPeopleAsync()
    {
        try
        {
            var rows = await Auth.GetAsync<List<NewChatPerson>>("/api/chat/users/directory");
            people = rows ?? [];
        }
        catch { people = []; }
        loading = false;
        if (!disposed) await InvokeAsync(StateHasChanged);
    }

    /// Debounced (250ms) server search. One character still filters the preloaded list.
    private async Task OnSearchChanged(ChangeEventArgs e)
    {
        query = e.Value?.ToString() ?? "";
        ResetSearch();
        var q = query.Trim();
        if (q.Length < 2) return;

        var cts = new CancellationTokenSource();
        searchCts = cts;
        searching = true;
        try
        {
            await Task.Delay(250, cts.Token);
            var rows = await Auth.GetAsync<List<NewChatPerson>>($"/api/chat/users/directory?q={Uri.EscapeDataString(q)}", cts.Token);
            if (cts.IsCancellationRequested) return;
            searchPeople = rows ?? [];
            searchFor = q;
        }
        catch (OperationCanceledException) { return; }
        catch
        {
            if (cts.IsCancellationRequested) return;
            searchPeople = null; // network hiccup: fall back to filtering the preloaded list
        }
        searching = false;
        StateHasChanged();
    }

    private void ResetSearch()
    {
        searchCts?.Cancel();
        searchCts?.Dispose();
        searchCts = null;
        searchPeople = null;
        searchFor = null;
        searching = false;
    }

    private void ClearSearch()
    {
        query = "";
        ResetSearch();
        focusPending = true;
    }

    private static bool Matches(NewChatPerson p, string q) =>
        q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (p.Email?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    private List<NewChatPerson> Visible
    {
        get
        {
            var q = query.Trim();
            var rows = (searchPeople is not null && searchFor == q ? searchPeople : people.Where(p => Matches(p, q)))
                .Take(80).ToList();
            if (step != NewChatStep.Members) return rows;
            // Picked people stay visible even when the current list doesn't hold them
            // (e.g. found by an earlier server search), so they can be unticked.
            var extra = picked.Where(m => rows.All(r => r.Id != m.Id) && Matches(m, q));
            return extra.Concat(rows).ToList();
        }
    }

    private bool IsPicked(Guid id) => picked.Any(m => m.Id == id);

    private async Task OnPersonClick(NewChatPerson p)
    {
        if (step == NewChatStep.Members) { Toggle(p); return; }
        if (startingDm is not null) return;
        startingDm = p.Id;
        try { await OnStartDm.InvokeAsync(p); }
        finally { startingDm = null; }
    }

    private void Toggle(NewChatPerson p)
    {
        if (IsPicked(p.Id)) picked.RemoveAll(m => m.Id == p.Id);
        else picked.Add(p);
        // Clearing the query after a pick mirrors WhatsApp: the next name starts fresh.
        if (query.Length > 0) { query = ""; ResetSearch(); }
        focusPending = true;
    }

    private void Remove(Guid id)
    {
        picked.RemoveAll(m => m.Id == id);
        focusPending = true;
    }

    private async Task OnSearchKey(KeyboardEventArgs e)
    {
        if (e.Key == "Backspace" && query.Length == 0 && step == NewChatStep.Members && picked.Count > 0)
        {
            picked.RemoveAt(picked.Count - 1);
            return;
        }
        if (e.Key == "Enter" && query.Trim().Length > 0)
        {
            var first = Visible.FirstOrDefault(p => step != NewChatStep.Members || !IsPicked(p.Id));
            if (first is not null) await OnPersonClick(first);
        }
    }

    private async Task OnNameKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && CanCreate) await CreateGroupAsync();
    }

    private void GoToMembers()
    {
        step = NewChatStep.Members;
        query = "";
        ResetSearch();
        focusPending = true;
    }

    private void GoToDetails()
    {
        if (picked.Count < MinGroupPicks) return;
        step = NewChatStep.Details;
        focusPending = true;
    }

    /// Back arrow and Escape: one step at a time; from the first step it closes the panel.
    /// Returns true while the panel stays open.
    public async Task<bool> Back()
    {
        switch (step)
        {
            case NewChatStep.Details:
                step = NewChatStep.Members;
                focusPending = true;
                StateHasChanged();
                return true;
            case NewChatStep.Members when !GroupMode:
                step = NewChatStep.Chat;
                picked.Clear();
                query = "";
                ResetSearch();
                focusPending = true;
                StateHasChanged();
                return true;
            default:
                await OnClose.InvokeAsync();
                return false;
        }
    }

    private async Task OnPhotoSelected(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file is null) return;
        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) { Toasts.Show("Choose an image file.", ToastKind.Warning); return; }
        if (file.Size > MaxPhotoBytes) { Toasts.Show("Photo must be 2 MB or less.", ToastKind.Warning); return; }
        try
        {
            using var ms = new MemoryStream();
            await using (var s = file.OpenReadStream(MaxPhotoBytes)) await s.CopyToAsync(ms);
            photoBytes = ms.ToArray();
            photoType = file.ContentType;
            photoName = file.Name;
            photoPreview = $"data:{file.ContentType};base64,{Convert.ToBase64String(photoBytes)}";
        }
        catch (Exception ex) { Toasts.Show("Could not read the photo: " + ex.Message, ToastKind.Error); }
    }

    private void ClearPhoto()
    {
        photoBytes = null;
        photoType = photoName = photoPreview = null;
    }

    private async Task CreateGroupAsync()
    {
        if (!CanCreate || OnCreateGroup is null) return;
        creating = true;
        StateHasChanged();
        try
        {
            var ok = await OnCreateGroup(new NewGroupDraft(groupName.Trim(), picked.ToList(), photoBytes, photoType, photoName));
            if (!ok && !disposed) creating = false;
        }
        catch
        {
            if (!disposed) creating = false;
            throw;
        }
    }

    private static string Subtitle(NewChatPerson p)
    {
        var parts = new[] { p.Department }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        return parts.Length > 0 ? string.Join(" · ", parts) : "";
    }

    private static string FirstName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        return name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private void OnPresenceChanged(string nik, string status)
    {
        var i = people.FindIndex(p => string.Equals(p.Nik, nik, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || people[i].PresenceStatus == status) return;
        people[i] = people[i] with { PresenceStatus = status };
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        disposed = true;
        Auth.PresenceChanged -= OnPresenceChanged;
        searchCts?.Cancel();
        searchCts?.Dispose();
    }
}
