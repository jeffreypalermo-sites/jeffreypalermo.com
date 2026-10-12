namespace JeffreyPalermo.UI.Server;

public sealed class SiteOptions
{
    public const string Section = "Site";

    /// <summary>
    /// The one host search engines should index (ADR-0024). Feeds, sitemaps, <c>robots.txt</c> and every page's
    /// <c>rel="canonical"</c> link name it, under whatever host a reader came by. <c>deploy/settings.json</c> names
    /// the same host, and a test holds the two equal.
    /// </summary>
    public string CanonicalHost { get; set; } = "www.jeffreypalermo.ceo";

    /// <summary>
    /// The hosts answered with a redirect to the same address on the canonical host (the rule <c>host-www</c>).
    /// None: no such name reaches the site (ADR-0024). <c>redirectHosts.sameAddress</c> in
    /// <c>deploy/settings.json</c> is the same list.
    /// </summary>
    public IList<string> HostsRedirectedToTheSameAddress { get; } = [];

    /// <summary>
    /// The hosts answered with a redirect to the canonical host's feed (the rule <c>host-feeds</c>). None, as above.
    /// <c>redirectHosts.feed</c> in <c>deploy/settings.json</c> is the same list.
    /// </summary>
    public IList<string> HostsRedirectedToTheFeed { get; } = [];

    /// <summary>Path to <c>content/</c>, absolute or relative to the content root. The container image ships it beside the app.</summary>
    public string ContentPath { get; set; } = "content";

    /// <summary>Identifies the content snapshot (the git commit in CI), used for ETags and diagnostics.</summary>
    public string Version { get; set; } = "dev";

    /// <summary>
    /// Path to the facts the Build wrote about this release, absolute or relative to the content root. The container
    /// image ships the file beside the app; <c>/_build</c> answers it (ADR-0012).
    /// </summary>
    public string BuildFactsPath { get; set; } = "build-facts.json";

    /// <summary>The name in the header, the document titles and the feeds, as the WordPress site had it.</summary>
    public string SiteTitle { get; set; } = "Programming with Palermo";

    /// <summary>The line under the site title.</summary>
    public string Tagline { get; set; } = "Jeffrey Palermo, Microsoft MVP, Author, Speaker, Clear Measure Chief Architect, Azure DevOps Expert";

    /// <summary>
    /// The ID of the Azure Front Door profile in front of the site, when there is one (its <c>X-Azure-FDID</c> header).
    /// Requests that carry it are believed about the host the visitor asked for; empty: no request is.
    /// </summary>
    public string FrontDoorId { get; set; } = string.Empty;

    public Uri BaseUri => new($"https://{CanonicalHost}/");

    /// <summary>The address of one of the site's paths on the canonical host.</summary>
    public string AbsoluteUrl(string path) => new Uri(BaseUri, path).AbsoluteUri;
}
