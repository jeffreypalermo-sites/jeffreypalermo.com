using System.Globalization;
using JeffreyPalermo.Core.Content;

namespace JeffreyPalermo.UI.Server.Presentation;

/// <summary>One page of a list: the posts of the home page or of an archive, or the posts and pages a search found.</summary>
/// <param name="Kind">What is listed; becomes the page's <c>body</c> class.</param>
/// <param name="Heading">The page's heading, e.g. <c>Monthly Archives:</c>.</param>
/// <param name="Subject">What the heading is about, e.g. <c>January 2020</c>; null on the home page.</param>
/// <param name="Title">The document title.</param>
/// <param name="Entries">What is listed, newest first. Only a search lists pages.</param>
/// <param name="PageHref">The address of page N of this listing.</param>
/// <param name="SearchText">The text searched for; null when the listing is not a search.</param>
public sealed record Listing(string Kind, string Heading, string? Subject, string Title, PagedList<Entry> Entries, Func<int, string> PageHref, string? SearchText = null)
{
    public string CanonicalPath => PageHref(Entries.Page);

    /// <summary>The next page: older posts, since every listing is newest first.</summary>
    public string? OlderHref => Entries.HasNext ? PageHref(Entries.Page + 1) : null;

    public string? NewerHref => Entries.HasPrevious ? PageHref(Entries.Page - 1) : null;

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
        return new("home", heading, null, Title(posts.Page, options.SiteTitle, options.Tagline), posts.Select(Entry.Of), page => SiteUrls.Page(SiteUrls.Home, page));
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
        return new("archive date", heading, subject, title, posts.Select(Entry.Of), page => SiteUrls.Page(path, page));
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
        return new($"archive {term.Taxonomy.Replace('_', '-')}", heading, term.Name, Title(posts.Page, term.Name, options.SiteTitle), posts.Select(Entry.Of), page => SiteUrls.Page(SiteUrls.Term(term), page));
    }

    public static Listing Search(SiteOptions options, string text, PagedList<Entry> found)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(found);
        return text.Length == 0
            ? new("search", "Search", null, Title(1, "Search", options.SiteTitle), found, _ => SiteUrls.Search, text)
            : new("search search-results", "Search Results for:", text, Title(found.Page, text, "Search Results", options.SiteTitle), found, page => SiteUrls.SearchResults(text, page), text);
    }

    /// <summary>The document title of page N of a listing: later pages say which they are.</summary>
    private static string Title(int page, params string[] parts)
    {
        var title = DisplayText.Title(parts);
        return page > 1 ? string.Create(CultureInfo.InvariantCulture, $"{title} | Page {page}") : title;
    }
}
