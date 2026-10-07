using JeffreyPalermo.UI.Server.Presentation;

namespace JeffreyPalermo.UnitTests.UI;

/// <summary>Dates and counts read as they did on the WordPress site.</summary>
public class DisplayTextTests
{
    [Theory]
    [InlineData("2020-01-09T16:09:00", "4:09 pm on January 9, 2020")]
    [InlineData("2008-07-29T08:08:44", "8:08 am on July 29, 2008")]
    [InlineData("2018-10-21T23:33:50", "11:33 pm on October 21, 2018")]
    [InlineData("2007-09-14T00:46:00", "12:46 am on September 14, 2007")]
    [InlineData("2007-09-14T12:00:00", "12:00 pm on September 14, 2007")]
    public void WritesAMomentAsTheThemeDid(string local, string expected) =>
        Assert.Equal(expected, DisplayText.Moment(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void NamesMonthsAndDaysInEnglish()
    {
        Assert.Equal("January 2020", DisplayText.Month(2020, 1));
        Assert.Equal("July 29, 2008", DisplayText.Day(2008, 7, 29));
    }

    [Fact]
    public void MachineReadableTimesSayWhenTheyAreUtc()
    {
        Assert.Equal("2008-07-29T08:08:44Z", DisplayText.Machine(new DateTime(2008, 7, 29, 8, 8, 44, DateTimeKind.Utc)));
        Assert.Equal("2009-01-13T09:14:29", DisplayText.Machine(new DateTime(2009, 1, 13, 9, 14, 29)));
    }

    [Theory]
    [InlineData(0, "0 comments")]
    [InlineData(1, "1 comment")]
    [InlineData(114, "114 comments")]
    public void CountsInTheSingularOnlyForOne(int count, string expected) =>
        Assert.Equal(expected, DisplayText.Count(count, "comment", "comments"));
}
