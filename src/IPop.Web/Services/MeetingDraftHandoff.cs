namespace IPop.Web.Services;

/// Meeting scheduling v3 §2 "More options…": the people picked in the booking sheet travel to the Calendar form in
/// memory (one scoped instance per tab), never in the address, so their keys never reach the address bar or history.
/// Calendar takes them once when it opens a /calendar?new=1&amp;kind=room form.
public sealed class MeetingDraftHandoff
{
    private List<InvitePersonDto>? people;

    public void Set(IEnumerable<InvitePersonDto> list) => people = list.ToList();

    /// The handed-over people, once (null when there are none); the handoff is empty afterwards.
    public List<InvitePersonDto>? Take()
    {
        var p = people;
        people = null;
        return p;
    }

    public bool HasPeople => people is { Count: > 0 };
}
