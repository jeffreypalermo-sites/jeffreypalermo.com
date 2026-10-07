using JeffreyPalermo.Core.Content;
using JeffreyPalermo.UI.Server.Presentation;

namespace JeffreyPalermo.UnitTests.UI;

/// <summary>The tag cloud keeps the sizes and the order WordPress gave it.</summary>
public class TagCloudTests
{
    private static TermUsage Tag(string name, int posts) =>
        new(new Term(name.GetHashCode(StringComparison.Ordinal), Taxonomies.Tag, name.Replace(' ', '-'), name, posts), posts);

    [Fact]
    public void SizesTagsBetween8And22PointsByTheLogarithmOfTheirPostCount()
    {
        // The counts and sizes of the live site's cloud on 2026-10-06.
        var cloud = TagCloud.Build([Tag("Tips & Tricks", 43), Tag("Developer Community", 35), Tag("Agile", 30), Tag("ASP.NET", 9), Tag("alt.net", 1)]);

        var sizes = cloud.ToDictionary(e => e.Term.Name, e => e.PointSize);
        Assert.Equal(22, sizes["Tips & Tricks"], precision: 3);
        Assert.Equal(21.164, sizes["Developer Community"], precision: 3);
        Assert.Equal(20.433, sizes["Agile"], precision: 3);
        Assert.Equal(15.313, sizes["ASP.NET"], precision: 3);
        Assert.Equal(8, sizes["alt.net"], precision: 3);
    }

    [Fact]
    public void WritesTheSizeAsCssPoints() =>
        Assert.Equal(["20.43pt", "8pt", "22pt"], TagCloud.Build([Tag("Tips & Tricks", 43), Tag("Agile", 30), Tag("alt.net", 1)]).Select(e => e.FontSize));

    [Fact]
    public void ListsTagsByNameIgnoringCaseAndSpaces() =>
        Assert.Equal(
            ["Agile", "alt.net", "ASP.NET", "asp.net mvc", "aspnetmvc", "partywithpalermo", "Party with Palermo", "Tools"],
            TagCloud.Build([Tag("Agile", 30), Tag("Tools", 15), Tag("partywithpalermo", 9), Tag("ASP.NET", 9), Tag("aspnetmvc", 3), Tag("Party with Palermo", 3), Tag("asp.net mvc", 2), Tag("alt.net", 1)])
                .Select(e => e.Term.Name));

    [Fact]
    public void ShowsOnlyTheMostUsedTags()
    {
        var tags = Enumerable.Range(1, TagCloud.MaximumTags + 5).Select(i => Tag($"tag-{i:D2}", 100 - i)).ToList();

        var cloud = TagCloud.Build(tags);

        Assert.Equal(TagCloud.MaximumTags, cloud.Count);
        Assert.DoesNotContain(cloud, e => e.PostCount < 100 - TagCloud.MaximumTags);
    }

    [Fact]
    public void TagsUsedEquallyOftenAreAllTheSmallestSize() =>
        Assert.All(TagCloud.Build([Tag("a", 3), Tag("b", 3)]), e => Assert.Equal(TagCloud.SmallestPoints, e.PointSize));

    [Fact]
    public void ASiteWithoutTagsHasAnEmptyCloud() =>
        Assert.Empty(TagCloud.Build([]));
}
