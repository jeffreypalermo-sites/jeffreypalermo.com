namespace JeffreyPalermo.UI.Server.Presentation;

/// <summary>One entry of the menu across the top of every page.</summary>
public sealed record MenuItem(string Text, string Href)
{
    public bool IsExternal => !Href.StartsWith('/');
}

/// <summary>
/// The menu. It was the WordPress site's "Header" custom menu until 2026-10-09 (ADR-0009). Since then it also leads
/// to the podcast, and to the posts about Jeffrey's books where it led to one book's listing on another site
/// (ADR-0021).
/// </summary>
public static class SiteMenu
{
    public static IReadOnlyList<MenuItem> Items { get; } =
    [
        new("Home", SiteUrls.Home),
        new("Blog", "/category/blog/"),
        new("AI DevOps Podcast", "/category/ai-devops-podcast/"),
        new("About Jeffrey Palermo", "/about/"),
        new("Onion Architecture", "/tag/onion-architecture/"),
        new("Clear Measure, Inc.", "https://www.clear-measure.com"),
        new("Books", "/tag/books/"),
    ];
}
