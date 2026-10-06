using JeffreyPalermo.Core.Urls;

namespace JeffreyPalermo.UnitTests.Core;

public class SlugTests
{
    [Theory]
    [InlineData("getting-started-with-the-asp.net-mvc-framework", "getting-started-with-the-asp-net-mvc-framework")]
    [InlineData("The-Onion-Architecture-Part-1", "the-onion-architecture-part-1")]
    [InlineData("i'll-get-to-your-application", "ill-get-to-your-application")]
    [InlineData("party%20with%20palermo", "party-with-palermo")]
    [InlineData("--a..b__c--", "a-b-c")]
    [InlineData("", "")]
    public void NormalizesTheWayWordPressSanitizesTitles(string input, string expected) =>
        Assert.Equal(expected, Slug.Normalize(input));
}
