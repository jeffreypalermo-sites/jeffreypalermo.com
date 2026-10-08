using System.Text.Json;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.UI.Server;
using JeffreyPalermo.UnitTests.Architecture;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// The custom domain is prepared and switched off (ADR-0014): the settings can list host names, the infrastructure
/// code and <c>deploy.ps1</c> know what to do with them, and no environment lists one until the DNS of
/// <c>jeffreypalermo.com</c> moves (<c>docs/runbooks/dns-cutover.md</c>).
/// </summary>
public class CustomDomainContractTests
{
    private static readonly string Root = DependencyRuleTests.RepositoryRoot();

    private static readonly string Bicep = File.ReadAllText(Path.Join(Root, "deploy", "infra", "main.bicep"));

    private static readonly string Module = File.ReadAllText(Path.Join(Root, "deploy", "infra", "custom-domains.bicep"));

    private static readonly string Deploy = File.ReadAllText(Path.Join(Root, "deploy", "deploy.ps1"));

    private static JsonElement Settings() =>
        JsonDocument.Parse(File.ReadAllText(Path.Join(Root, "deploy", "settings.json"))).RootElement;

    private static List<string> HostNames(string environment) =>
        Settings().GetProperty("environments").GetProperty(environment).TryGetProperty("hostNames", out var names)
            ? [.. names.EnumerateArray().Select(name => name.GetString()!)]
            : [];

    /// <summary>
    /// Nothing is bound before the DNS move. The pull request of that day changes <c>settings.json</c> and the last
    /// line here together: prod then lists jeffreypalermo.com, www.jeffreypalermo.com and feeds.jeffreypalermo.com.
    /// </summary>
    [Theory]
    [InlineData("tdd")]
    [InlineData("uat")]
    [InlineData("prod")]
    public void NoEnvironmentHasAHostNameUntilTheDnsMoves(string environment) =>
        Assert.Empty(HostNames(environment));

    [Theory]
    [InlineData("tdd")]
    [InlineData("uat")]
    [InlineData("prod")]
    public void OnlyAnEnvironmentBehindAFrontDoorMayListHostNames(string environment)
    {
        var place = Settings().GetProperty("environments").GetProperty(environment);

        Assert.True(place.GetProperty("frontDoor").GetBoolean() || HostNames(environment).Count == 0, $"{environment} has no Front Door to take a host name.");
    }

    /// <summary>deploy.ps1 sorts the host names by this name; the site redirects www. and feeds. of the same name.</summary>
    [Fact]
    public void TheSettingsNameTheCanonicalHostTheSiteRedirectsTo()
    {
        var canonical = Settings().GetProperty("canonicalHost").GetString();

        Assert.Equal(new SiteOptions().CanonicalHost, canonical);
        Assert.Equal("jeffreypalermo.com", canonical);
    }

    [Fact]
    public void TheScriptTreatsAsRedirectHostsExactlyTheNamesTheSiteRedirects()
    {
        var resolver = new LegacyUrlResolver("jeffreypalermo.com");
        var site = Core.ContentBuilder.Site();

        Assert.Contains("$redirectHosts = @(if ($canonicalHost) { \"www.$canonicalHost\"; \"feeds.$canonicalHost\" })", Deploy, StringComparison.Ordinal);
        Assert.True(LegacyUrlResolver.DecidedByHost(resolver.Resolve(new UrlRequest("www.jeffreypalermo.com", "/"), site)));
        Assert.True(LegacyUrlResolver.DecidedByHost(resolver.Resolve(new UrlRequest("feeds.jeffreypalermo.com", "/"), site)));
        Assert.False(LegacyUrlResolver.DecidedByHost(resolver.Resolve(new UrlRequest("jeffreypalermo.com", "/"), site)));
        // No other first label is redirected by the site: a name like uat.jeffreypalermo.com is answered with pages.
        Assert.All((string[])["uat", "blog", "feed", "ww", "wwww"], label =>
            Assert.False(LegacyUrlResolver.DecidedByHost(resolver.Resolve(new UrlRequest($"{label}.jeffreypalermo.com", "/"), site))));
    }

    [Fact]
    public void TheHostNamesAreCheckedBeforeAzureIsAsked()
    {
        var check = Deploy.IndexOf("FAIL the host names of '$Environment' in settings.json cannot be deployed", StringComparison.Ordinal);
        var azure = Deploy.IndexOf("az account show", StringComparison.Ordinal);

        Assert.True(check >= 0 && check < azure, "deploy.ps1 must refuse bad host names before its first call to Azure.");
    }

    [Fact]
    public void TheTemplateTakesTheHostNamesAsTwoListsThatAreEmptyByDefault()
    {
        Assert.Matches(@"param hostNames array = \[\]", Bicep);
        Assert.Matches(@"param redirectHostNames array = \[\]", Bicep);
        Assert.Contains("hostNames         = @{ value = @($pageHostNames) }", Deploy, StringComparison.Ordinal);
        Assert.Contains("redirectHostNames = @{ value = @($redirectHostNames) }", Deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptChangesNothingInDns()
    {
        // The records are printed for a person to enter where the zone is hosted. No DNS command is in the script.
        Assert.DoesNotMatch(@"network\s+dns|'dns'|record-set", Deploy);
        Assert.DoesNotContain("Microsoft.Network/dnsZones", Bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.Network/dnsZones", Module, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EverythingAHostNameNeedsIsInOneFileThatIsDeployedOnlyWithAHostName()
    {
        Assert.Matches(@"module customDomains 'custom-domains\.bicep' = if \(customDomain\)", Bicep);
        Assert.Contains("var customDomain = frontDoor && !empty(concat(hostNames, redirectHostNames))", Bicep, StringComparison.Ordinal);
        Assert.Contains("output hostNames array = customDomain ? customDomains!.outputs.hostNames : []", Bicep, StringComparison.Ordinal);
        // The file itself has what the names need; main.bicep has none of it.
        Assert.Contains("Microsoft.Cdn/profiles/customDomains@", Module, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Cdn/profiles/customDomains@", Bicep, StringComparison.Ordinal);
        Assert.Contains("certificateType: 'ManagedCertificate'", Module, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackageOfAReleaseCarriesTheWholeInfrastructureFolder()
    {
        // deploy.ps1 hands main.bicep to the Azure CLI, which compiles the module beside it.
        var package = File.ReadAllText(Path.Join(Root, "scripts", "build-deploy-package.sh"));

        Assert.Contains("cp -R \"$root/deploy/.\" \"$out/\"", package, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(Root, "deploy", "infra", "custom-domains.bicep")));
    }

    [Fact]
    public void TheRunbookAndTheDecisionAreWhereTheScriptSaysTheyAre()
    {
        Assert.Contains("docs/runbooks/dns-cutover.md", Deploy, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(Root, "docs", "runbooks", "dns-cutover.md")));
        Assert.True(File.Exists(Path.Join(Root, "docs", "adr", "0014-the-custom-domain-prepared.md")));
    }
}
