using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JeffreyPalermo.Core.Content;
using JeffreyPalermo.Infrastructure.Content;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>One body of the content tree as it is stored: of a post, a page or a comment.</summary>
/// <param name="Name">The content file, relative to the content directory; a comment as <c>{file}#comment-{id}</c>.</param>
/// <param name="Text">The body: what follows the front matter, or the comment's markup.</param>
/// <param name="IsMarkdown">True for a body written in Markdown.</param>
/// <param name="CanBeWritten">
/// False for a comment in a file that is not written the way this tool writes it: writing it again would change more
/// than the comment, so what is returned for it is not kept.
/// </param>
public sealed record ContentBody(string Name, string Text, bool IsMarkdown, bool CanBeWritten);

/// <summary>
/// Walks the post, page and comment bodies of a content tree and writes back the ones a caller changes. Only the
/// body is handed out and only the body is written: the front matter, the line endings and every other byte of a
/// file stay as they are, so a change shows nothing but what the caller changed (posts are edited in git, ADR-0010).
/// </summary>
public static partial class ContentBodies
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Hands every body to <paramref name="rewrite"/>, in the order of the files, and writes a file when what comes
    /// back differs. Answers how many files were written.
    /// </summary>
    public static async Task<int> RewriteAsync(ContentLayout layout, Func<ContentBody, Task<string>> rewrite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rewrite);
        var changed = 0;
        foreach (var file in Files(layout.PostsDirectory).Concat(Files(layout.PagesDirectory)))
        {
            var name = Path.GetRelativePath(layout.Root, file).Replace('\\', '/');
            var wrote = file.EndsWith(".comments.json", StringComparison.OrdinalIgnoreCase)
                ? await RewriteCommentsAsync(file, name, rewrite, cancellationToken).ConfigureAwait(false)
                : await RewriteBodyAsync(file, name, rewrite, cancellationToken).ConfigureAwait(false);
            changed += wrote ? 1 : 0;
        }

        return changed;
    }

    /// <summary>Hands every body to <paramref name="read"/>, in the order of the files, and writes nothing.</summary>
    public static async Task ReadAsync(ContentLayout layout, Action<ContentBody> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        await RewriteAsync(
            layout,
            body =>
            {
                read(body);
                return Task.FromResult(body.Text);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> Files(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".comments.json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
            : [];

    // A post or a page: front matter, then the body. Only the body is read, and only the body is written.
    private static async Task<bool> RewriteBodyAsync(string file, string name, Func<ContentBody, Task<string>> rewrite, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(file, Utf8, cancellationToken).ConfigureAwait(false);
        var frontMatter = FrontMatter().Match(text);
        if (!frontMatter.Success)
        {
            return false;
        }

        var body = text[frontMatter.Length..];
        var rewritten = await rewrite(new ContentBody(name, body, file.EndsWith(".md", StringComparison.OrdinalIgnoreCase), true)).ConfigureAwait(false);
        if (rewritten == body)
        {
            return false;
        }

        await File.WriteAllTextAsync(file, string.Concat(text.AsSpan(0, frontMatter.Length), rewritten), Utf8, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // The comments of a post. The file is written again only when writing it unchanged would give the same bytes, so
    // that a change shows nothing but what the caller changed.
    private static async Task<bool> RewriteCommentsAsync(string file, string name, Func<ContentBody, Task<string>> rewrite, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(file, Utf8, cancellationToken).ConfigureAwait(false);
        List<Comment>? comments;
        try
        {
            comments = JsonSerializer.Deserialize<List<Comment>>(text, ContentJson.Options);
        }
        catch (JsonException)
        {
            // The loader reports a file it cannot read; there is nothing to change in it.
            return false;
        }

        if (comments is null || comments.Count == 0)
        {
            return false;
        }

        var writable = Written(comments) == text;
        var changed = false;
        for (var i = 0; i < comments.Count; i++)
        {
            var rewritten = await rewrite(new ContentBody($"{name}#comment-{comments[i].Id}", comments[i].ContentHtml, false, writable)).ConfigureAwait(false);
            if (writable && rewritten != comments[i].ContentHtml)
            {
                comments[i] = comments[i] with { ContentHtml = rewritten };
                changed = true;
            }
        }

        if (changed)
        {
            await File.WriteAllTextAsync(file, Written(comments), Utf8, cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private static string Written(List<Comment> comments) => JsonSerializer.Serialize(comments, ContentJson.Options).ReplaceLineEndings("\n") + "\n";

    [GeneratedRegex(@"\A---\r?\n.*?\r?\n---\r?\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();
}
