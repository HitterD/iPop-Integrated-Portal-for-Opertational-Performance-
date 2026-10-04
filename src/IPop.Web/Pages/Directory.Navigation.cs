using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using IPop.Web.Services;
using IPop.Web.Models;

namespace IPop.Web.Pages;

public partial class Directory
{
    private readonly DirectoryNavigation navigation = new();
    private int requestGeneration, favoriteGeneration;
    private bool disposed, sheetSynced;
    private IJSObjectReference? sheetModule;
    private string? navigationAccount;
    private string? justExpandedKey;
    private async Task CleanupSheet() { if (sheetModule is not null) { try { await sheetModule.InvokeVoidAsync("close"); await sheetModule.DisposeAsync(); } catch (JSException) { } } }
    private void OnAccountChanged() => _ = InvokeAsync(async () =>
    {
        if (navigationAccount == Auth.Me?.Nik) return;
        requestGeneration++; favoriteGeneration++;
        people = []; favoritePeople = []; selected = null; selectedNiks.Clear();
        navigation.Expanded.Clear(); navigation.SelectedKey = null; navigation.ShowEmpty = false; navigation.Search = "";
        selection = null; favOnly = false; searchQ = null; presenceFilter = ""; selectedSort = "name-asc";
        await CloseTree();
        await RestoreNavigation(); await LoadFavoritesAsync(); await Reload(1, replace: true);
    });
    private string NavigationKey => $"ipop.directory.navigation.v1.{Uri.EscapeDataString(Auth.Me?.Nik ?? "anonymous")}";
    private sealed record NavigationSaved(string? SelectedKey, string[] Expanded, bool ShowEmpty);
    private string QueryParameters => SelectionParam
        + (searchQ is null ? "" : $"&q={Uri.EscapeDataString(searchQ)}")
        + (string.IsNullOrEmpty(presenceFilter) ? "" : $"&presence={presenceFilter}")
        + $"&sort={selectedSort.Split('-')[0]}&desc={(selectedSort == "name-desc" ? "true" : "false")}&favorites={(favOnly ? "true" : "false")}&grouped={(selection is not null ? "true" : "false")}";
    private void SyncSelection() => selection = navigation.Selected is { } node ? new(node.Kind, node.Code, node.Name, node.SiteCode) : null;
    private async Task RestoreNavigation()
    {
        navigationAccount = Auth.Me?.Nik;
        try
        {
            var raw = await JS.InvokeAsync<string?>("localStorage.getItem", NavigationKey);
            if (raw is not null && System.Text.Json.JsonSerializer.Deserialize<NavigationSaved>(raw) is { } saved)
            {
                navigation.Expanded = (saved.Expanded ?? []).ToHashSet(); navigation.SelectedKey = saved.SelectedKey; navigation.ShowEmpty = saved.ShowEmpty;
                if (dirStructure is not null) navigation.Load(dirStructure);
                SyncSelection();
            }
        }
        catch (JSException) { }
        catch (System.Text.Json.JsonException) { }
        await SaveNavigation();
    }
    private async Task SaveNavigation()
    {
        if (Auth.Me?.Nik is null) return;
        try { await JS.InvokeVoidAsync("localStorage.setItem", NavigationKey, System.Text.Json.JsonSerializer.Serialize(new NavigationSaved(navigation.SelectedKey, navigation.Expanded.ToArray(), navigation.ShowEmpty))); }
        catch (JSException) { }
    }
    private async Task ToggleNode(DirectoryNavNode node)
    {
        if (!string.IsNullOrWhiteSpace(navigation.Search)) return;
        // #23: only the children of the node just opened fade in (never the whole tree).
        if (!navigation.Expanded.Remove(node.Key)) { navigation.Expanded.Add(node.Key); justExpandedKey = node.Key; }
        else justExpandedKey = null;
        await SaveNavigation();
    }
    private async Task CollapseAll() { navigation.Search = ""; navigation.Expanded.Clear(); await SaveNavigation(); }
    private async Task SetShowEmpty(ChangeEventArgs e) { navigation.ShowEmpty = e.Value is true; await SaveNavigation(); }
    private async Task SelectOccurrence(DirectoryNavNode node)
    {
        navigation.SelectedKey = node.Key; SyncSelection(); selectedNiks.Clear();
        await CloseTree(); await SaveNavigation(); await Reload(1, replace: true);
    }
    private Task ToggleTree() { treeOpen = !treeOpen; return Task.CompletedTask; }
    private Task CloseTree() { treeOpen = false; return Task.CompletedTask; }
    private Task OnTreeKey(KeyboardEventArgs e) => e.Key == "Escape" ? CloseTree() : Task.CompletedTask;
    private bool IsGroupStart(Employee person) { var index = people.IndexOf(person); return index == 0 || index > 0 && people[index - 1].UnitKey != person.UnitKey; }
    private static string GroupLabel(Employee person) => person.UnitName ?? "—";
    private int GroupLoaded(Employee person) => people.Count(p => p.UnitKey == person.UnitKey);
    private async Task SyncDepartmentSheet()
    {
        sheetModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/directory-navigation.js");
        if (treeOpen == sheetSynced) return;
        _selfRef ??= DotNetObjectReference.Create(this);
        await sheetModule.InvokeVoidAsync(treeOpen ? "open" : "close", _selfRef);
        sheetSynced = treeOpen;
    }
    [JSInvokable] public Task CloseDepartments() => InvokeAsync(async () => { await CloseTree(); StateHasChanged(); });
}
