// Notes page — "Repeat…": a copy of this page is made on a schedule (NotesRecurrenceWorker). The popover lives in Notes.razor (Pop.Repeat).
using System.Net.Http.Json;
using IPop.Application.Notes;
using IPop.Web.Services;

namespace IPop.Web.Pages;

public partial class Notes
{
    private NoteRecurrenceDto? repeatNow;
    private string repeatKind = "workdays";
    private readonly HashSet<string> repeatDays = ["MO"];
    private int repeatMonthDay = 1;

    private static readonly (string Key, string Label)[] RepeatKinds = [("daily", "Daily"), ("workdays", "Weekdays"), ("weekly", "Days"), ("monthly", "Monthly")];
    private static readonly (string Code, string Label)[] RepeatDays =
        [("MO", "Monday"), ("TU", "Tuesday"), ("WE", "Wednesday"), ("TH", "Thursday"), ("FR", "Friday"), ("SA", "Saturday"), ("SU", "Sunday")];

    private async Task LoadRepeatAsync()
    {
        repeatNow = null;
        if (note is null || note.Kind != "page" || note.IsGuide) return;
        var id = note.Id;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Get, $"/api/notes/{id}/repeat");
            // 204 = this page does not repeat; anything else that is not a body is left as "does not repeat" too
            if (res.StatusCode != System.Net.HttpStatusCode.OK || note?.Id != id) return;
            repeatNow = await res.Content.ReadFromJsonAsync<NoteRecurrenceDto>(Json);
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException) { }
    }

    private async Task BeginRepeat()
    {
        if (note is null || !HasEditAccess) return;
        var pageId = note.Id;
        // what is stored fills the form; a page that does not repeat yet starts on "Weekdays"
        await LoadRepeatAsync();
        if (disposed || note?.Id != pageId) return;
        if (repeatNow is { } r) ReadRule(r.Rule);
        else { repeatKind = "workdays"; repeatDays.Clear(); repeatDays.Add("MO"); repeatMonthDay = 1; }
        pop = Pop.Repeat;
    }

    private void ReadRule(string rule)
    {
        repeatDays.Clear();
        if (rule == "FREQ=DAILY") repeatKind = "daily";
        else if (rule == "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR") repeatKind = "workdays";
        else if (rule.StartsWith("FREQ=WEEKLY;BYDAY=", StringComparison.Ordinal))
        {
            repeatKind = "weekly";
            foreach (var d in rule["FREQ=WEEKLY;BYDAY=".Length..].Split(',')) repeatDays.Add(d);
        }
        else if (rule.StartsWith("FREQ=MONTHLY;BYMONTHDAY=", StringComparison.Ordinal))
        {
            repeatKind = "monthly";
            repeatMonthDay = int.TryParse(rule["FREQ=MONTHLY;BYMONTHDAY=".Length..], out var d) ? d : 1;
        }
        else { repeatKind = "weekly"; repeatDays.Add("MO"); }
    }

    private void ToggleRepeatDay(string code) { if (!repeatDays.Remove(code)) repeatDays.Add(code); }

    /// The same words the server will show afterwards ("Every Monday and Wednesday").
    private string RepeatSummary
    {
        get
        {
            if (repeatKind == "weekly" && repeatDays.Count == 0) return "Pick at least one day.";
            var order = RepeatDays.Select(d => d.Code).ToList();
            var rule = NoteRecurrenceRules.FromChoice(repeatKind, repeatDays.OrderBy(d => order.IndexOf(d)).ToList(), repeatMonthDay);
            return rule is null ? "Pick at least one day." : $"{NoteRecurrenceRules.Describe(rule)}, from today.";
        }
    }

    private async Task SaveRepeatAsync()
    {
        if (note is null || !HasEditAccess) return;
        if (repeatKind == "weekly" && repeatDays.Count == 0) return;
        var pageId = note.Id;
        busy = true;
        try
        {
            var order = RepeatDays.Select(d => d.Code).ToList();
            var res = await Auth.SendAsync(HttpMethod.Put, $"/api/notes/{pageId}/repeat",
                new SetNoteRecurrenceRequest(repeatKind, repeatKind == "weekly" ? repeatDays.OrderBy(d => order.IndexOf(d)).ToList() : null, repeatKind == "monthly" ? repeatMonthDay : null));
            if (disposed || note?.Id != pageId) return;
            if (res.IsSuccessStatusCode)
            {
                repeatNow = await res.Content.ReadFromJsonAsync<NoteRecurrenceDto>(Json);
                pop = Pop.None;
                Toasts.Show($"This page now repeats: {repeatNow?.Description ?? "saved"}", ToastKind.Success);
            }
            else
            {
                var code = await ErrorCodeAsync(res);
                Toasts.Show(code switch
                {
                    "too_many" => $"You can keep up to {NoteRecurrenceRules.MaxRulesPerPerson} repeating pages. Stop one first.",
                    "forbidden" or "not_found" or "locked" => "You need edit access to make this page repeat",
                    "not_a_page" => "Only a text page can repeat",
                    _ => "Couldn't set up the repeat",
                }, ToastKind.Error);
            }
        }
        finally { busy = false; }
    }

    private async Task StopRepeatAsync()
    {
        if (note is null || !HasEditAccess) return;
        var pageId = note.Id;
        busy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/notes/{pageId}/repeat");
            if (disposed || note?.Id != pageId) return;
            if (res.IsSuccessStatusCode) { repeatNow = null; pop = Pop.None; Toasts.Show("This page no longer repeats", ToastKind.Success); }
            else Toasts.Show("Couldn't stop the repeat", ToastKind.Error);
        }
        finally { busy = false; }
    }
}
