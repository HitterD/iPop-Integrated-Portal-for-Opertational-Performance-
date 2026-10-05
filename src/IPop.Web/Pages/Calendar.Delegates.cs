// Calendar sharing stage 2 — delegates: "Calendars I manage" in the rail switches the whole view
// to that person's calendar (yellow banner), new events are created in their name, and a full
// delegate answers the owner's invites. The owner's delegate list lives in the settings dialog.
using IPop.Web.Services;

namespace IPop.Web.Pages;

public partial class Calendar
{
    public record ManagedCalendar(string Nik, string Name, string? Department, string Level);

    private List<ManagedCalendar> managed = [];
    private ManagedCalendar? managing;

    private bool CanCreate => managing is null || managing.Level is "edit" or "full";

    private string FeedUrl(DateTime from, DateTime to)
    {
        var range = $"from={Uri.EscapeDataString(from.ToUniversalTime().ToString("o"))}&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("o"))}";
        return managing is null ? $"/api/calendar/feed?{range}" : $"/api/calendar/managed/{Uri.EscapeDataString(managing.Nik)}/feed?{range}";
    }

    private async Task LoadManagedAsync()
    {
        try { managed = await Auth.GetAsync<List<ManagedCalendar>>("/api/calendar/managed") ?? []; }
        catch { /* delegates are additive: the own calendar still works without them */ }
    }

    private async Task ManageAsync(ManagedCalendar? m)
    {
        if (PersonMode) { Nav.NavigateTo(m is null ? "/calendar" : $"/calendar?manage={PLinks.Token(m.Nik)}"); return; }
        if (managing?.Nik == m?.Nik) return;
        managing = m;
        CloseDrawer();
        items = [];
        await LoadAsync();
    }

    private static string LevelLabel(string level) => level == "full" ? "You act on their behalf" : "You can edit their events";

    // ── Drawer: on-behalf labels and answering for the owner ──

    /// The invitee a full delegate may answer for (their reply row), or null. An organizer doesn't reply.
    private Person? BehalfRow => detail is { DelegateLevel: "full", DelegateForNik: { } nik } && nik != Auth.Me?.Nik && nik != detail.OwnerNik
        ? detail.Attendees.FirstOrDefault(a => a.Nik == nik) : null;

    private Task RsvpForAsync(string response) => BehalfRow is { } row ? RsvpAsync(response, row.Nik) : Task.CompletedTask;
}
