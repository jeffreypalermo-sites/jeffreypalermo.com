using System.Text.Json;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.UI.Server;
using JeffreyPalermo.UnitTests.Architecture;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// The custom domain (ADR-0014): the settings can list host names, and the infrastructure code and
/// <c>deploy.ps1</c> know what to do with them. uat and production each list a name of their own in
/// <c>jeffreypalermo.ceo</c> (ADR-0018), and production's is the canonical host (ADR-0024). Production lists no
/// name of <c>jeffreypalermo.com</c>: that domain stays with WordPress.com (<c>docs/runbooks/dns-cutover.md</c>).
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
    /// Which environment lists which host names. uat and production each have their own name in jeffreypalermo.ceo
    /// (Jeffrey, 2026-10-08, ADR-0018). tdd has no Front Door to take one: its name there is a forwarding at the
    /// domain's DNS host, not a host name of the site. No name of jeffreypalermo.com is listed: the site stays at
    /// jeffreypalermo.ceo (Jeffrey, 2026-10-12, ADR-0024).
    /// </summary>
    [Theory]
    [InlineData("tdd")]
    [InlineData("uat", "uat.jeffreypalermo.ceo")]
    [InlineData("prod", "www.jeffreypalermo.ceo")]
    public void EachEnvironmentListsTheHostNamesThatWereDecided(string environment, params string[] hostNames) =>
        Assert.Equal(hostNames, HostNames(environment));

    private static List<string> RedirectHosts(string kind) =>
        [.. Settings().GetProperty("redirectHosts").GetProperty(kind).EnumerateArray().Select(name => name.GetString()!)];

    /// <summary>The site's rules as the settings state them: the canonical host and the hosts it redirects.</summary>
    private static LegacyUrlResolver ResolverOfTheSettings() =>
        new(Settings().GetProperty("canonicalHost").GetString()!, RedirectHosts("sameAddress"), RedirectHosts("feed"));

    /// <summary>
    /// An environment's own name is one the site answers with pages, never one it redirects: uat's name too, which
    /// is not the canonical host (ADR-0024). It is a name of jeffreypalermo.ceo and of no other domain, and no two
    /// environments share one.
    /// </summary>
    [Theory]
    [InlineData("uat")]
    [InlineData("prod")]
    public void AnEnvironmentsOwnNameIsAnsweredWithPagesAndIsANameOfJeffreyPalermoCeo(string environment)
    {
        var name = Assert.Single(HostNames(environment));

        Assert.EndsWith(".jeffreypalermo.ceo", name, StringComparison.Ordinal);
        Assert.False(LegacyUrlResolver.DecidedByHost(ResolverOfTheSettings().Resolve(new UrlRequest(name, "/"), Core.ContentBuilder.Site())));
        Assert.DoesNotContain(name, RedirectHosts("sameAddress").Concat(RedirectHosts("feed")));
        Assert.DoesNotContain(name, HostNames(environment == "uat" ? "prod" : "uat"));
    }

    /// <summary>
    /// A name that waits for its records must fail nothing. The script purges only a name that serves, and asks for
    /// a new token where the old one is of no use, without ever failing for it.
    /// </summary>
    [Fact]
    public void ANameThatWaitsIsNotPurgedAndItsTokenIsRenewedWithoutFailing()
    {
        Assert.Contains("Where-Object { [bool] $_.kept -and \"$($_.validationState)\" -in 'Approved', 'PendingRevalidation' }", Deploy, StringComparison.Ordinal);
        Assert.Contains("$domains = @(([Uri] $frontDoorUrl).Host) + @($servingHostNames)", Deploy, StringComparison.Ordinal);
        Assert.Contains("'resource', 'invoke-action', '--action', 'refreshValidationToken', '--ids', $id, '--api-version', '2024-02-01'", Deploy, StringComparison.Ordinal);
        Assert.Contains("if (-not ($state -in 'TimedOut', 'Rejected' -or $tooOld -or $renewal)) { continue }", Deploy, StringComparison.Ordinal);

        // Between the stack's outputs and the purge, the script leaves in one way only: a region that does not run
        // the release. Nothing about a host name ends a deployment.
        var from = Deploy.IndexOf("$servingHostNames = @(", StringComparison.Ordinal);
        var to = Deploy.IndexOf("$purgeFile =", StringComparison.Ordinal);
        Assert.True(from >= 0 && from < to);
        Assert.Equal(1, Deploy[from..to].Split("exit 1").Length - 1);
        Assert.Contains("does not run release $Version; the Front Door's cache was not emptied", Deploy[from..to], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tdd")]
    [InlineData("uat")]
    [InlineData("prod")]
    public void OnlyAnEnvironmentBehindAFrontDoorMayListHostNames(string environment)
    {
        var place = Settings().GetProperty("environments").GetProperty(environment);

        Assert.True(place.GetProperty("frontDoor").GetBoolean() || HostNames(environment).Count == 0, $"{environment} has no Front Door to take a host name.");
    }

    /// <summary>
    /// The canonical host (ADR-0024) is production's own name. The settings and the site name the same one: the
    /// site writes it into feeds, sitemaps, robots.txt and every page's canonical link.
    /// </summary>
    [Fact]
    public void TheSettingsAndTheSiteNameProductionsNameAsTheCanonicalHost()
    {
        var canonical = Settings().GetProperty("canonicalHost").GetString()!;

        Assert.Equal("www.jeffreypalermo.ceo", canonical);
        Assert.Equal(new SiteOptions().CanonicalHost, canonical);
        Assert.Equal([canonical], HostNames("prod"));
        Assert.DoesNotContain(canonical, HostNames("uat"));
    }

    /// <summary>
    /// One source says which names are answered with a redirect: two lists, which the settings state for
    /// deploy.ps1 and the site has as its own. Both are empty (ADR-0024). Nothing is derived from the canonical host
    /// any more: it begins with www. itself.
    /// </summary>
    [Fact]
    public void TheScriptTreatsAsRedirectHostsExactlyTheNamesTheSiteRedirects()
    {
        var options = new SiteOptions();

        Assert.Equal(options.HostsRedirectedToTheSameAddress, RedirectHosts("sameAddress"));
        Assert.Equal(options.HostsRedirectedToTheFeed, RedirectHosts("feed"));
        Assert.Empty(RedirectHosts("sameAddress"));
        Assert.Empty(RedirectHosts("feed"));
        Assert.Contains("foreach ($kind in 'sameAddress', 'feed') { if ($statedRedirects.ContainsKey($kind)) { $statedRedirects[$kind] } }", Deploy, StringComparison.Ordinal);
        Assert.Contains("$pageHostNames = @($hostNames | Where-Object { $_ -notin $redirectHosts })", Deploy, StringComparison.Ordinal);
        Assert.Contains("$redirectHostNames = @($hostNames | Where-Object { $_ -in $redirectHosts })", Deploy, StringComparison.Ordinal);
        Assert.DoesNotContain("www.$canonicalHost", Deploy, StringComparison.Ordinal);
        Assert.DoesNotContain("feeds.$canonicalHost", Deploy, StringComparison.Ordinal);
        // The site is given the same two lists and nothing else to decide by.
        Assert.Contains("new LegacyUrlResolver(options.CanonicalHost, options.HostsRedirectedToTheSameAddress, options.HostsRedirectedToTheFeed)", File.ReadAllText(Path.Join(Root, "src", "UI.Server", "Program.cs")), StringComparison.Ordinal);
    }

    /// <summary>With the settings as they are, the site redirects no host: not the canonical host, not www. or feeds. of anything.</summary>
    [Theory]
    [InlineData("www.jeffreypalermo.ceo")]
    [InlineData("uat.jeffreypalermo.ceo")]
    [InlineData("www.www.jeffreypalermo.ceo")]
    [InlineData("feeds.www.jeffreypalermo.ceo")]
    [InlineData("feeds.jeffreypalermo.ceo")]
    [InlineData("jeffreypalermo.ceo")]
    [InlineData("www.jeffreypalermo.com")]
    [InlineData("feeds.jeffreypalermo.com")]
    public void WithTheSettingsAsTheyAreNoHostIsAnsweredWithARedirect(string host) =>
        Assert.False(LegacyUrlResolver.DecidedByHost(ResolverOfTheSettings().Resolve(new UrlRequest(host, "/"), Core.ContentBuilder.Site())));

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
