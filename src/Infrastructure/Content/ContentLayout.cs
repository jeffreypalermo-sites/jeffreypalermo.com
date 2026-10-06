using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.Infrastructure.Content;

/// <summary>Where each kind of content lives under the repository's <c>content/</c> root.</summary>
public sealed class ContentLayout(string root)
{
    public string Root { get; } = root;

    public string PostsDirectory => Path.Join(Root, "posts");
    public string PagesDirectory => Path.Join(Root, "pages");
    public string UploadsDirectory => Path.Join(Root, "uploads");
    public string AttachmentsFile => Path.Join(Root, "archive", "attachments.json");
    public string TermsFile => Path.Join(Root, "archive", "terms.json");

    /// <summary><c>/2008/07/the-onion-architecture-part-1/</c> → <c>posts/2008/07/the-onion-architecture-part-1.html</c>.</summary>
    public string PostFile(string permalink, ContentFormat format)
    {
        var segments = Segments(permalink);
        if (segments.Length != 3)
        {
            throw new ArgumentException($"Post permalink must be /yyyy/mm/slug/: '{permalink}'.", nameof(permalink));
        }

        return Path.Join(PostsDirectory, segments[0], segments[1], segments[2] + Extension(format));
    }

    public static string CommentsFile(string postFile) => Path.ChangeExtension(postFile, ".comments.json");

    public string PageFile(string permalink, ContentFormat format) =>
        Path.Join(PagesDirectory, string.Join('-', Segments(permalink)) + Extension(format));

    /// <summary><c>/wp-content/uploads/2018/06/a.png</c> → <c>uploads/2018/06/a.png</c>.</summary>
    public string UploadFile(string uploadPath)
    {
        const string prefix = "/wp-content/uploads/";
        if (!uploadPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Not an uploads path: '{uploadPath}'.", nameof(uploadPath));
        }

        var segments = Uri.UnescapeDataString(uploadPath[prefix.Length..]).Split('/');
        if (segments.Any(s => s is ".." or "." || s.Contains('\\', StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Upload path must not traverse directories: '{uploadPath}'.", nameof(uploadPath));
        }

        return Path.Join([UploadsDirectory, .. segments]);
    }

    private static string[] Segments(string permalink) =>
        permalink.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Extension(ContentFormat format) => format == ContentFormat.Html ? ".html" : ".md";
}
