using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.FrontMatter;

namespace JeffreyPalermo.UnitTests.Infrastructure;

public class FrontMatterDocumentTests
{
    private static readonly PostMetadata Sample = new()
    {
        WpId = 945,
        Title = "The Onion Architecture : part 1",
        Slug = "the-onion-architecture-part-1",
        Permalink = "/2008/07/the-onion-architecture-part-1/",
        Date = new DateTime(2008, 7, 29, 8, 8, 44, DateTimeKind.Unspecified),
        DateUtc = new DateTime(2008, 7, 29, 13, 8, 44, DateTimeKind.Utc),
        Format = ContentFormat.Html,
        Tags = ["onion-architecture"],
        CommentsOpen = true,
    };

    [Fact]
    public void RoundTripsMetadataAndBody()
    {
        var text = FrontMatterDocument.Write(Sample, "<p>Hello</p>");
        var (metadata, body) = FrontMatterDocument.Read<PostMetadata>(text);

        Assert.Equivalent(Sample, metadata);
        Assert.Equal("<p>Hello</p>\n", body);
    }

    [Fact]
    public void WritesDatesWithoutShiftingTimeZonesAndEnumsInLowerCase()
    {
        var text = FrontMatterDocument.Write(Sample, "x");

        Assert.Contains("date: 2008-07-29T08:08:44\n", text, StringComparison.Ordinal);
        Assert.Contains("date_utc: 2008-07-29T13:08:44Z\n", text, StringComparison.Ordinal);
        Assert.Contains("format: html\n", text, StringComparison.Ordinal);
        Assert.Contains("wp_id: 945\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("categories", text, StringComparison.Ordinal);
        Assert.StartsWith("---\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsCrLfFiles()
    {
        var text = FrontMatterDocument.Write(Sample, "line1\nline2").Replace("\n", "\r\n", StringComparison.Ordinal);
        var (metadata, body) = FrontMatterDocument.Read<PostMetadata>(text);

        Assert.Equal(945, metadata.WpId);
        Assert.Equal("line1\nline2\n", body);
    }

    [Theory]
    [InlineData("no front matter")]
    [InlineData("---\ntitle: x\nbody without closing fence")]
    public void RejectsMalformedDocuments(string text) =>
        Assert.Throws<FormatException>(() => FrontMatterDocument.Read<PostMetadata>(text));
}
