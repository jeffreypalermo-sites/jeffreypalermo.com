using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UnitTests.Core;

public class PermalinkTests
{
    [Fact]
    public void ParsesTheCanonicalShape()
    {
        var permalink = Permalink.Parse("/2008/07/the-onion-architecture-part-1/");

        Assert.Equal((2008, 7, "the-onion-architecture-part-1"), (permalink.Year, permalink.Month, permalink.Slug));
        Assert.Equal("/2008/07/the-onion-architecture-part-1/", permalink.Path);
        Assert.Equal(permalink.Path, permalink.ToString());
    }

    [Fact]
    public void KeepsPercentEncodedSlugsExactlyAsWordPressStoredThem()
    {
        const string path = "/2011/03/yes-sometimes-%e5%a5%bd%e6%96%87%e7%ab%a0-level-100/";

        Assert.Equal(path, Permalink.Parse(path).Path);
    }

    [Theory]
    [InlineData("/2008/07/the-onion-architecture-part-1")]
    [InlineData("2008/07/slug/")]
    [InlineData("/2008/13/slug/")]
    [InlineData("/2008/00/slug/")]
    [InlineData("/1899/07/slug/")]
    [InlineData("/2008/7/slug/")]
    [InlineData("/2008/07/Upper-Case/")]
    [InlineData("/2008/07/has.dot/")]
    [InlineData("/2008/07/a/b/")]
    [InlineData("/about/")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsAnythingElse(string? path)
    {
        Assert.False(Permalink.TryParse(path, out _));
        if (path is not null)
        {
            Assert.Throws<FormatException>(() => Permalink.Parse(path));
        }
    }

    [Fact]
    public void IsAValueObject() =>
        Assert.Equal(Permalink.Create(2008, 7, "a"), Permalink.Parse("/2008/07/a/"));

    [Fact]
    public void CreateValidatesItsParts() =>
        Assert.Throws<ArgumentException>(() => Permalink.Create(2008, 7, "Not Valid"));
}
