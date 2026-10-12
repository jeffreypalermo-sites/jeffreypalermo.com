using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// Hosts UI.Server in-process over the repository's real <c>content/</c> tree, with the settings it is deployed
/// with: the canonical host is the app's own default, and no host is answered with a redirect (ADR-0024).
/// </summary>
public sealed class SiteFactory : WebApplicationFactory<Program>
{
    /// <summary>The canonical host of the site as it is deployed. <c>CustomDomainContractTests</c> holds it equal to the app's default.</summary>
    public const string CanonicalHost = "www.jeffreypalermo.ceo";

    /// <summary>What every absolute address the site gives for itself begins with.</summary>
    public const string CanonicalOrigin = "https://" + CanonicalHost;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Site:ContentPath", TestPaths.Content);
    }

    /// <summary>A client that does not follow redirects, addressed to the given host.</summary>
    public HttpClient ClientFor(string host = CanonicalHost) => SiteHosts.ClientFor(this, host);
}

/// <summary>
/// The site given hosts to redirect. It is deployed with none (ADR-0024), so the two rules by host, and what the
/// cache and the Front Door's forwarded host mean for them, are held here with the settings a move of
/// jeffreypalermo.com would state: that domain the canonical host, its <c>www.</c> answered with the same address
/// there, its <c>feeds.</c> with the feed.
/// </summary>
internal static class SiteHosts
{
    public const string CanonicalHostOfTheMove = "jeffreypalermo.com";

    public static WebApplicationFactory<Program> WithTheHostsOfTheMove(this WebApplicationFactory<Program> factory, Action<IWebHostBuilder>? more = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Site:CanonicalHost", CanonicalHostOfTheMove);
            builder.UseSetting("Site:HostsRedirectedToTheSameAddress:0", "www.jeffreypalermo.com");
            builder.UseSetting("Site:HostsRedirectedToTheFeed:0", "feeds.jeffreypalermo.com");
            more?.Invoke(builder);
        });

    /// <summary>A client that does not follow redirects, addressed to the given host.</summary>
    public static HttpClient ClientFor(this WebApplicationFactory<Program> factory, string host) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri($"https://{host}/") });
}

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRoot();

    public static string Content => Path.Join(RepositoryRoot, "content");

    public static string Contract => Path.Join(RepositoryRoot, "tests", "contract");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "JeffreyPalermo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root (JeffreyPalermo.slnx).");
    }
}
