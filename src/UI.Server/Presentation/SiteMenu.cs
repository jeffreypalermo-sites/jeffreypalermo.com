namespace JeffreyPalermo.UI.Server.Presentation;

/// <summary>One entry of the menu across the top of every page.</summary>
public sealed record MenuItem(string Text, string Href)
{
    public bool IsExternal => !Href.StartsWith('/');
}

/// <summary>The menu the WordPress site had (its "Header" custom menu), in the same order.</summary>
public static class SiteMenu
{
    public static IReadOnlyList<MenuItem> Items { get; } =
    [
        new("Home", SiteUrls.Home),
        new("Blog", "/category/blog/"),
        new("About Jeffrey Palermo", "/about/"),
        new("Onion Architecture", "/tag/onion-architecture/"),
        new("Clear Measure, Inc.", "https://www.clear-measure.com"),
        new(".NET DevOps for Azure", "https://bookauthority.org/books/new-azure-devops-books"),
    ];
}
