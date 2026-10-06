using System.Globalization;
using System.Net;
using System.Text;
using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UI.Server.Endpoints;

/// <summary>
/// Plain HTML for build step 2: enough to prove every legacy URL lands on real content. Blazor static SSR pages
/// replace these renderings in build step 3; the routes and status codes stay the same.
/// </summary>
internal static class Html
{
    public static IResult Page(string title, string canonicalPath, string body) =>
        Results.Content(
            $"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>{Encode(title)}</title><link rel="canonical" href="{Encode(canonicalPath)}"></head>
            <body>
            {body}
            </body>
            </html>
            """,
            "text/html; charset=utf-8");

    public static IResult Post(Post post) =>
        Page(post.Title, post.Permalink.Path, $"<article><h1>{Encode(post.Title)}</h1>{post.HtmlBody}{Comments(post.Comments)}</article>");

    public static IResult Listing(string title, string canonicalPath, PagedList<Post> posts) =>
        Page(title, canonicalPath, $"<h1>{Encode(title)}</h1><ul>{string.Concat(posts.Items.Select(Item))}</ul>");

    public static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string Item(Post post) =>
        $"<li><a href=\"{Encode(post.Permalink.Path)}\">{Encode(post.Title)}</a> <time>{post.Published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}</time></li>";

    private static string Comments(IReadOnlyList<Comment> comments)
    {
        if (comments.Count == 0)
        {
            return string.Empty;
        }

        var html = new StringBuilder("<section><h2>Comments</h2>");
        foreach (var comment in comments)
        {
            html.Append(CultureInfo.InvariantCulture, $"<div id=\"comment-{comment.Id}\"><p>{Encode(comment.AuthorName)}</p>{comment.ContentHtml}</div>");
        }

        return html.Append("</section>").ToString();
    }
}
