using System.Globalization;
using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UI.Server.Presentation;

/// <summary>One page of a list of posts: the home page, an archive, or search results.</summary>
/// <param name="Kind">What is listed; becomes the page's <c>body</c> class.</param>
/// <param name="Heading">The page's heading, e.g. <c>Monthly Archives:</c>.</param>
/// <param name="Subject">What the heading is about, e.g. <c>January 2020</c>; null on the home page.</param>
/// <param name="Title">The document title.</param>
/// <param name="PageHref">The address of page N of this listing.</param>
/// <param name="SearchText">The text searched for; null when the listing is not a search.</param>
public sealed record Listing(string Kind, string Heading, string? Subject, string Title, PagedList<Post> Posts, Func<int, string> PageHref, string? SearchText = null)
{
    public string CanonicalPath => PageHref(Posts.Page);

    /// <summary>The next page: older posts, since every listing is newest first.</summary>
    public string? OlderHref => Posts.HasNext ? PageHref(Posts.Page + 1) : null;

    public string? NewerHref => Posts.HasPrevious ? PageHref(Posts.Page - 1) : null;

    public bool IsSearch => SearchText is not null;
}

/// <summary>Builds each kind of listing with the headings and titles the WordPress theme gave it.</summary>
public static class Listings
{
    public static Listing Home(SiteOptions options, PagedList<Post> posts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(posts);
        var heading = posts.Page > 1 ? string.Create(CultureInfo.InvariantCulture, $"Recent Updates Page {posts.Page}") : "Recent Updates";
        return new("home", heading, null, Title(posts.Page, options.SiteTitle, options.Tagline), posts, page => SiteUrls.Page(SiteUrls.Home, page));
    }

    public static Listing Date(SiteOptions options, int year, int? month, int? day, PagedList<Post> posts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(posts);
        var yearText = year.ToString("D4", CultureInfo.InvariantCulture);
        var monthName = month is { } m ? new DateTime(year, m, 1).ToString("MMMM", CultureInfo.InvariantCulture) : string.Empty;
        var (heading, subject, path, title) = (month, day) switch
        {
            ({ } mm, { } dd) => ("Daily Archives:", DisplayText.Day(year, mm, dd), SiteUrls.Day(year, mm, dd), Title(posts.Page, dd.ToString(CultureInfo.InvariantCulture), monthName, yearText, options.SiteTitle)),
            ({ } mm, null) => ("Monthly Archives:", DisplayText.Month(year, mm), SiteUrls.Month(year, mm), Title(posts.Page, monthName, yearText, options.SiteTitle)),
            _ => ("Yearly Archives:", yearText, SiteUrls.Year(year), Title(posts.Page, yearText, options.SiteTitle)),
        };
        return new("archive date", heading, subject, title, posts, page => SiteUrls.Page(path, page));
    }

    public static Listing Term(SiteOptions options, Term term, PagedList<Post> posts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(term);
        ArgumentNullException.ThrowIfNull(posts);
        var heading = term.Taxonomy switch
        {
            Taxonomies.Category => "Category Archives:",
            Taxonomies.Tag => "Tag Archives:",
            Taxonomies.Author => "Author Archives:",
            _ => "Archives:",
        };
        return new($"archive {term.Taxonomy.Replace('_', '-')}", heading, term.Name, Title(posts.Page, term.Name, options.SiteTitle), posts, page => SiteUrls.Page(SiteUrls.Term(term), page));
    }

    public static Listing Search(SiteOptions options, string text, PagedList<Post> posts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(posts);
        return text.Length == 0
            ? new("search", "Search", null, Title(1, "Search", options.SiteTitle), posts, _ => SiteUrls.Search, text)
            : new("search search-results", "Search Results for:", text, Title(posts.Page, text, "Search Results", options.SiteTitle), posts, page => SiteUrls.SearchResults(text, page), text);
    }

    /// <summary>The document title of page N of a listing: later pages say which they are.</summary>
    private static string Title(int page, params string[] parts)
    {
        var title = DisplayText.Title(parts);
        return page > 1 ? string.Create(CultureInfo.InvariantCulture, $"{title} | Page {page}") : title;
    }
}
