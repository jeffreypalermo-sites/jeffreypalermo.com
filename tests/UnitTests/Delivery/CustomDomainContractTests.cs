using System.Text.Json;
using JeffreyPalermo.Core.Urls;
using JeffreyPalermo.UI.Server;
using JeffreyPalermo.UnitTests.Architecture;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// The custom domain (ADR-0014): the settings can list host names, and the infrastructure code and
/// <c>deploy.ps1</c> know what to do with them. uat and production each list a name of their own in
/// <c>jeffreypalermo.ceo</c> (ADR-0018). Production also lists the three names of <c>jeffreypalermo.com</c>, whose
/// DNS moves by <c>docs/runbooks/dns-cutover.md</c>.
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
    /// domain's DNS host, not a host name of the site. Production also lists the three names the DNS move gives to
    /// the site (<c>docs/runbooks/dns-cutover.md</c>, "Before the day", step 4). Going back before the day takes those
    /// three out of the line for prod here and out of <c>settings.json</c> together.
    /// </summary>
    [Theory]
    [InlineData("tdd")]
    [InlineData("uat", "uat.jeffreypalermo.ceo")]
    [InlineData("prod", "jeffreypalermo.com", "www.jeffreypalermo.com", "feeds.jeffreypalermo.com", "www.jeffreypalermo.ceo")]
    public void EachEnvironmentListsTheHostNamesThatWereDecided(string environment, params string[] hostNames) =>
        Assert.Equal(hostNames, HostNames(environment));

    /// <summary>
    /// An environment's own name is one the site answers with pages, never one it redirects: www. of another domain
    /// is not www. of the canonical host. It is no name of the domain whose DNS moves, and no two environments
    /// share one.
    /// </summary>
    [Theory]
    [InlineData("uat")]
    [InlineData("prod")]
    public void AnEnvironmentsOwnNameIsAnsweredWithPagesAndIsNoNameOfTheDomainThatMoves(string environment)
    {
        var canonical = Settings().GetProperty("canonicalHost").GetString()!;
        var resolver = new LegacyUrlResolver(canonical);
        var name = Assert.Single(HostNames(environment), listed => !(listed == canonical || listed.EndsWith($".{canonical}", StringComparison.Ordinal)));

        Assert.EndsWith(".jeffreypalermo.ceo", name, StringComparison.Ordinal);
        Assert.False(LegacyUrlResolver.DecidedByHost(resolver.Resolve(new UrlRequest(name, "/"), Core.ContentBuilder.Site())));
        Assert.DoesNotContain(name, HostNames(environment == "uat" ? "prod" : "uat"));
    }

    /// <summary>
    /// Production's names of the domain that moves are the canonical host, which the site answers with pages, and
    /// exactly the names the site redirects to it. No other environment lists a name of that domain.
    /// </summary>
    [Fact]
    public void ProductionListsTheCanonicalHostAndEveryNameTheSiteRedirectsToIt()
    {
        var canonical = Settings().GetProperty("canonicalHost").GetString()!;
        var resolver = new LegacyUrlResolver(canonical);
        var site = Core.ContentBuilder.Site();
        bool OfTheDomain(string name) => name == canonical || name.EndsWith($".{canonical}", StringComparison.Ordinal);
        var names = HostNames("prod").Where(OfTheDomain).ToList();

        Assert.Equal([canonical, $"www.{canonical}", $"feeds.{canonical}"], names);
        Assert.Equal(canonical, Settings().GetProperty("environments").GetProperty("prod").GetProperty("dnsZone").GetString());
        Assert.False(LegacyUrlResolver.DecidedByHost(resolver.Resolve(new UrlRequest(names[0], "/"), site)));
        Assert.All(names.Skip(1), name => Assert.True(LegacyUrlResolver.DecidedByHost(resolver.Resolve(new UrlRequest(name, "/"), site))));
        Assert.DoesNotContain(HostNames("uat").Concat(HostNames("tdd")), OfTheDomain);
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
