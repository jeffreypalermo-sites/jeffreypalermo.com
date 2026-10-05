using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.UnitTests.Infrastructure;

public class ContentLayoutTests
{
    private readonly ContentLayout _layout = new("content");

    [Fact]
    public void MapsPostPermalinkToYearMonthSlugFile() =>
        Assert.Equal(
            Path.Join("content", "posts", "2008", "07", "the-onion-architecture-part-1.html"),
            _layout.PostFile("/2008/07/the-onion-architecture-part-1/", ContentFormat.Html));

    [Fact]
    public void MarkdownPostsUseMdExtension() =>
        Assert.EndsWith(".md", _layout.PostFile("/2026/10/new-post/", ContentFormat.Markdown), StringComparison.Ordinal);

    [Fact]
    public void RejectsNonPostPermalinks() =>
        Assert.Throws<ArgumentException>(() => _layout.PostFile("/about/", ContentFormat.Html));

    [Fact]
    public void CommentsLiveBesideTheirPost() =>
        Assert.Equal(
            Path.Join("content", "posts", "2008", "07", "a.comments.json"),
            ContentLayout.CommentsFile(Path.Join("content", "posts", "2008", "07", "a.html")));

    [Fact]
    public void MapsUploadsAndUnescapesFileNames() =>
        Assert.Equal(
            Path.Join("content", "uploads", "2018", "06", "my image.png"),
            _layout.UploadFile("/wp-content/uploads/2018/06/my%20image.png"));

    [Theory]
    [InlineData("/wp-content/uploads/../../secrets.txt")]
    [InlineData("/wp-content/uploads/2018/%2e%2e/x.png")]
    [InlineData("/somewhere/else.png")]
    public void RejectsUploadPathsOutsideUploads(string path) =>
        Assert.Throws<ArgumentException>(() => _layout.UploadFile(path));
}
