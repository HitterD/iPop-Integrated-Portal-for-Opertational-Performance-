using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

public class CalendarLinksTests
{
    [Fact]
    public void Prefill_link_carries_source_title_people_and_private_focus_time_without_exposing_employee_ids()
    {
        var people = new PeopleLinks();
        var links = new CalendarLinks(people);
        var start = new DateTime(2026, 10, 12, 2, 0, 0, DateTimeKind.Utc);
        var url = links.NewMeeting("Review & plan", ["EMP001", "EMP002", "EMP001"], start: start, minutes: 60,
            isPrivate: true, notes: "Related task", from: "From project");
        var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://ipop.test" + url).Query);
        Assert.Equal("1", q["new"]);
        Assert.Equal("Review & plan", q["title"]);
        Assert.Equal(["EMP001", "EMP002"], people.Niks(q["people"]));
        Assert.DoesNotContain("EMP001", url);
        Assert.DoesNotContain("EMP002", url);
        Assert.Equal("60", q["mins"]);
        Assert.Equal("1", q["private"]);
        Assert.Equal("Related task", q["notes"]);
        Assert.Equal("From project", q["from"]);
        Assert.Equal(start, DateTime.Parse(q["start"]!, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public void Empty_values_are_omitted_and_long_text_is_bounded()
    {
        var links = new CalendarLinks(new PeopleLinks());
        Assert.Equal("/calendar?new=1", links.NewMeeting());
        var q = System.Web.HttpUtility.ParseQueryString(new Uri("https://ipop.test" + links.NewMeeting(new string('x', 400), notes: new string('y', 3000), from: new string('z', 100))).Query);
        Assert.Equal(200, q["title"]!.Length);
        Assert.Equal(1500, q["notes"]!.Length);
        Assert.Equal(80, q["from"]!.Length);
    }
}
