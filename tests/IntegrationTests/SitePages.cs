using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>Fetches a page of the in-process site and parses it, so tests can ask about elements instead of text.</summary>
internal static class SitePages
{
    /// <summary>Post, page and comment bodies: migrated HTML the layout does not own.</summary>
    public const string Bodies = ".entry-content, .comment-content";

    public static async Task<IDocument> GetPageAsync(this HttpClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        return Parse(await response.Content.ReadAsStringAsync());
    }

    public static IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>The elements the layout and the page components wrote: everything outside the migrated bodies.</summary>
    public static IEnumerable<IElement> Chrome(this IDocument document, string selector) =>
        document.QuerySelectorAll(selector).Where(element => element.Closest(Bodies) is null);

    /// <summary>The text of the one element the selector finds, with runs of white space as one space.</summary>
    public static string Text(this IParentNode node, string selector) =>
        string.Join(' ', Assert.Single(node.QuerySelectorAll(selector)).TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string? Href(this IParentNode node, string selector) => node.QuerySelector(selector)?.GetAttribute("href");
}
