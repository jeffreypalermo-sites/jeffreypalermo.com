using System.Text.Json;
using JeffreyPalermo.Core;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.FrontMatter;
using Markdig;
using YamlDotNet.Core;

namespace JeffreyPalermo.Infrastructure.Content;

/// <summary>
/// Loads <c>content/</c> into the <see cref="SiteContent"/> aggregate: front matter files for posts and pages
/// (<c>.html</c> as-is, <c>.md</c> rendered by Markdig), comments beside each post, and the archive JSON files.
/// File-level problems and domain invariant violations are all reported together. It also tells the domain which
/// files <c>uploads/</c> holds and which are listed as lost, so that a picture that leads nowhere fails the load.
/// </summary>
public sealed class FileSystemContentSource(ContentLayout layout, string version) : ISiteContentSource
{
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public async Task<SiteContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var posts = new List<Post>();
        var pages = new List<Page>();

        foreach (var file in ContentFiles(layout.PostsDirectory))
        {
            var post = await TryLoadAsync(file, errors, (frontMatter, html) => ToPost(file, frontMatter, html), cancellationToken).ConfigureAwait(false);
            if (post is not null)
            {
                var comments = await ReadJsonAsync<List<Comment>>(ContentLayout.CommentsFile(file), errors, cancellationToken).ConfigureAwait(false);
                posts.Add(post with { Comments = comments ?? [] });
            }
        }

        foreach (var file in ContentFiles(layout.PagesDirectory))
        {
            var page = await TryLoadAsync(file, errors, (frontMatter, html) => ToPage(frontMatter, html), cancellationToken).ConfigureAwait(false);
            if (page is not null)
            {
                pages.Add(page);
            }
        }

        var attachments = await ReadJsonAsync<List<Attachment>>(layout.AttachmentsFile, errors, cancellationToken).ConfigureAwait(false) ?? [];
        var terms = await ReadJsonAsync<List<Term>>(layout.TermsFile, errors, cancellationToken).ConfigureAwait(false) ?? [];
        var redirects = await ReadJsonAsync<List<LegacyRedirect>>(layout.LegacyRedirectsFile, errors, cancellationToken).ConfigureAwait(false) ?? [];
        var lostUploads = await ReadJsonAsync<List<string>>(layout.LostUploadsFile, errors, cancellationToken).ConfigureAwait(false) ?? [];

        if (errors.Count > 0)
        {
            throw new ContentValidationException(errors);
        }

        return SiteContent.Create(version, posts, pages, attachments, terms, redirects, new SiteFiles(layout.UploadPaths(), lostUploads));
    }

    private static IEnumerable<string> ContentFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
            : [];

    private Post ToPost(string file, PostFrontMatter frontMatter, string html)
    {
        var permalink = Permalink.Parse(frontMatter.Permalink);
        var expectedFile = layout.PostFile(permalink.Path, FormatOf(file));
        if (!string.Equals(Path.GetFullPath(expectedFile), Path.GetFullPath(file), StringComparison.Ordinal))
        {
            throw new FormatException($"permalink {permalink.Path} belongs in {Path.GetRelativePath(layout.Root, expectedFile)}");
        }

        return new Post
        {
            WpId = frontMatter.WpId,
            Permalink = permalink,
            Title = frontMatter.Title,
            Published = frontMatter.Date,
            PublishedUtc = frontMatter.DateUtc ?? throw new FormatException("date_utc is required"),
            Modified = frontMatter.Modified,
            HtmlBody = html,
            Excerpt = frontMatter.Excerpt,
            AuthorSlug = frontMatter.Author ?? throw new FormatException("author is required"),
            CategorySlugs = frontMatter.Categories,
            TagSlugs = frontMatter.Tags,
            PostFormat = frontMatter.PostFormat,
            FeaturedMediaId = frontMatter.FeaturedMediaId,
            CommentsOpen = frontMatter.CommentsOpen,
        };
    }

    private static Page ToPage(PostFrontMatter frontMatter, string html) => new()
    {
        WpId = frontMatter.WpId,
        Path = frontMatter.Permalink,
        Title = frontMatter.Title,
        HtmlBody = html,
        PublishedUtc = frontMatter.DateUtc,
        Modified = frontMatter.Modified,
        Excerpt = frontMatter.Excerpt,
    };

    private async Task<T?> TryLoadAsync<T>(string file, List<string> errors, Func<PostFrontMatter, string, T> map, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var (frontMatter, body) = FrontMatterDocument.Read<PostFrontMatter>(text);
            var html = FormatOf(file) == ContentFormat.Markdown ? Markdig.Markdown.ToHtml(body, Markdown) : body;
            return map(frontMatter, html.Trim());
        }
        catch (Exception ex) when (ex is FormatException or YamlException or ArgumentException)
        {
            errors.Add($"{Relative(file)}: {ex.Message}");
            return null;
        }
    }

    private async Task<T?> ReadJsonAsync<T>(string file, List<string> errors, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<T>(stream, ContentJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            errors.Add($"{Relative(file)}: {ex.Message}");
            return null;
        }
    }

    private static ContentFormat FormatOf(string file) =>
        file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? ContentFormat.Markdown : ContentFormat.Html;

    private string Relative(string file) => Path.GetRelativePath(layout.Root, file).Replace('\\', '/');
}
