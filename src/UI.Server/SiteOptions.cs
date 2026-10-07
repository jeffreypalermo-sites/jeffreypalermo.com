namespace JeffreyPalermo.UI.Server;

public sealed class SiteOptions
{
    public const string Section = "Site";

    /// <summary>The one host search engines should index; <c>www.</c> and <c>feeds.</c> redirect here.</summary>
    public string CanonicalHost { get; set; } = "jeffreypalermo.com";

    /// <summary>Path to <c>content/</c>, absolute or relative to the content root. The container image ships it beside the app.</summary>
    public string ContentPath { get; set; } = "content";

    /// <summary>Identifies the content snapshot (the git commit in CI), used for ETags and diagnostics.</summary>
    public string Version { get; set; } = "dev";

    public string SiteTitle { get; set; } = "Jeffrey Palermo";

    /// <summary>
    /// The ID of the Azure Front Door profile in front of the site, when there is one (its <c>X-Azure-FDID</c> header).
    /// Requests that carry it are believed about the host the visitor asked for; empty: no request is.
    /// </summary>
    public string FrontDoorId { get; set; } = string.Empty;

    public Uri BaseUri => new($"https://{CanonicalHost}/");
}
