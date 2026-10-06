using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>Hosts UI.Server in-process over the repository's real <c>content/</c> tree.</summary>
public sealed class SiteFactory : WebApplicationFactory<Program>
{
    public const string CanonicalHost = "jeffreypalermo.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Site:ContentPath", TestPaths.Content);
        builder.UseSetting("Site:CanonicalHost", CanonicalHost);
    }

    /// <summary>A client that does not follow redirects, addressed to the given host.</summary>
    public HttpClient ClientFor(string host = CanonicalHost) =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri($"https://{host}/") });
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
