using IPop.Web.Models;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Directory
{
    private string phoneTab = "people", phoneQuery = "", phoneScope = "All";
    private bool phoneOptions, phoneDepartmentDetail, phoneLoading, phoneLoadingMore, phoneLoaded, phoneLoadError, phoneSelecting;
    private List<Employee> phonePeople = [];
    private int phoneGeneration, phoneBulkGeneration;
    private static readonly IReadOnlyList<IPop.Web.Components.Phone.SegmentItem> phoneTabs = [new("people", "People"), new("departments", "Departments")];
    private IReadOnlyList<Employee> PhonePeople => phonePeople.Where(p =>
        (!favOnly || Favorites.IsFavorite(p.Nik)) &&
        (presenceFilter.Length == 0 || p.Status == presenceFilter) &&
        (phoneQuery.Trim().Length == 0 || $"{p.Name} {p.Department} {p.Section} {p.Site} {p.Extension}".Contains(phoneQuery.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
    private IEnumerable<DirectoryNavNode> PhoneMatches => phoneQuery.Trim().Length == 0 ? [] : navigation.Nodes.Where(n => n.Count > 0 && n.Name.Contains(phoneQuery.Trim(), StringComparison.OrdinalIgnoreCase));
    private IEnumerable<DirectoryNavNode> PhoneNodes => PhoneMatches.Where(n => phoneScope switch { "Sites" => n.Kind == "site", "Departments" => n.Kind != "site", _ => true });
    private int PhoneScopeCount(string scope) => scope switch { "People" => PhonePeople.Count, "Departments" => PhoneMatches.Count(n => n.Kind != "site"), "Sites" => PhoneMatches.Count(n => n.Kind == "site"), _ => PhonePeople.Count + PhoneMatches.Count() };
    private void PhoneChanged() => _ = InvokeAsync(StateHasChanged);
    private void PhoneSearch(ChangeEventArgs e) { phoneQuery = e.Value?.ToString() ?? ""; phoneScope = "All"; }
    private void ClearPhoneSearch() { phoneQuery = ""; phoneScope = "All"; }
    private void SetPhoneTab(string tab) { phoneTab = tab; phoneQuery = ""; phoneScope = "All"; }
    private void SetPhonePresence(string status) { presenceFilter = status; phoneOptions = false; }
    private async Task PhoneJump(string letter) => await JS.InvokeVoidAsync("ipop.phone.directoryJump", letter);
    private async Task LoadPhonePeople()
    {
        phoneLoading = true; phoneLoadError = false; phoneLoaded = true;
        var generation = ++phoneGeneration; var account = Auth.Me?.Nik;
        try
        {
            // First screenful at once (≈50 KB), then the rest in the background: the full list is ≈2 MB,
            // slow on mobile data, and "Loading people…" stayed up until all of it arrived.
            var first = await Auth.GetAsync<DirectoryPageDto>("/api/directory/employees?page=1&pageSize=120&sort=name");
            if (disposed || generation != phoneGeneration || account != Auth.Me?.Nik) return;
            if (first is null) { phoneLoadError = true; return; }
            phonePeople = first.Items.Select(Map).DistinctBy(p => p.Nik).ToList();
            if (phonePeople.Count >= first.Total || first.Items.Length == 0) return;
            phoneLoading = false;
            phoneLoadingMore = true;
            await InvokeAsync(StateHasChanged);
            var loaded = new List<Employee>();
            for (var p = 1; ; p++)
            {
                var result = await Auth.GetAsync<DirectoryPageDto>($"/api/directory/employees?page={p}&pageSize=5000&sort=name");
                if (disposed || generation != phoneGeneration || account != Auth.Me?.Nik) return;
                if (result is null) return; // keep the first page; the next visit retries
                loaded.AddRange(result.Items.Select(Map));
                if (loaded.Count >= result.Total || result.Items.Length == 0) break;
            }
            phonePeople = loaded.DistinctBy(p => p.Nik).ToList();
        }
        finally { if (generation == phoneGeneration) { phoneLoading = false; phoneLoadingMore = false; await InvokeAsync(StateHasChanged); } }
    }
    private async Task OpenPhoneDepartment(DirectoryNavNode node)
    {
        phoneQuery = ""; phoneScope = "All";
        await SelectOccurrence(node); phoneDepartmentDetail = true;
    }
    private void ClosePhoneDepartment() { phoneBulkGeneration++; phoneDepartmentDetail = false; phoneTab = "departments"; }
    private async Task PhoneDepartmentAction(string action)
    {
        // Freeze the scope and account for the entire paged action. A new action, selection,
        // account transition or page disposal invalidates any response already in flight.
        var account = Auth.Me?.Nik;
        var selectedKey = navigation.SelectedKey;
        var selectedScope = SelectionParam;
        var selectedName = selection?.Name;
        var generation = ++phoneBulkGeneration;
        bool IsCurrent() => !disposed && account is not null && account == Auth.Me?.Nik
            && generation == phoneBulkGeneration && selectedKey == navigation.SelectedKey
            && selectedScope == SelectionParam && phoneDepartmentDetail;
        var members = await DirectoryMemberFetch.LoadAsync(
            page => Auth.GetAsync<DirectoryPageDto>($"/api/directory/employees?{selectedScope}&page={page}&pageSize=5000"), IsCurrent);
        if (!IsCurrent()) return;
        if (members is null) { Toasts.Show("Could not load all members.", ToastKind.Error); return; }
        if (members.Count == 0) return;
        var tokens = PLinks.Tokens(members);
        if (!IsCurrent()) return;
        Nav.NavigateTo(action switch { "chat" => $"/chat?new=group&members={tokens}", "mail" => $"/mail?compose=1&to={tokens}", _ => CalLinks.NewMeeting(niks: members, from: $"Meeting with {selectedName}") });
    }
}
