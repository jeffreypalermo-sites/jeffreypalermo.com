using System.Globalization;
using System.Text.RegularExpressions;
using JeffreyPalermo.UI.Server;
using JeffreyPalermo.UnitTests.Architecture;

namespace JeffreyPalermo.UnitTests.Delivery;

/// <summary>
/// The Front Door keeps the site's answers and a deployment empties it (ADR-0013). These tests pin what that rests
/// on in the infrastructure code, in the scripts the pipeline runs, and in the decision record itself.
/// <c>DeployScriptTests</c> runs the scripts; a real Front Door is first seen in uat.
/// </summary>
public partial class CacheContractTests
{
    private static readonly string Root = DependencyRuleTests.RepositoryRoot();

    private static readonly string Bicep = File.ReadAllText(Path.Join(Root, "deploy", "infra", "main.bicep"));

    private static readonly string Deploy = File.ReadAllText(Path.Join(Root, "deploy", "deploy.ps1"));

    private static readonly string TestSite = File.ReadAllText(Path.Join(Root, "deploy", "test-site.ps1"));

    private static readonly string Decision = File.ReadAllText(Path.Join(Root, "docs", "adr", "0013-the-front-door-keeps-the-sites-answers.md"));

    [Fact]
    public void TheRouteKeepsAnswersByAddressAndQueryString()
    {
        // /?p=945 is a post, /?s=onion a search: an edge that ignored the query string would answer both with the home page.
        Assert.Matches(@"var routeCache = \{[^}]*queryStringCachingBehavior:\s*'UseQueryString'", Bicep);
        Assert.Matches(@"resource route [^{]*\{[\s\S]*?cacheConfiguration: routeCache", Bicep);
        Assert.DoesNotContain("IgnoreQueryString", Bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("queryParameters", Bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRuleOverridesWhatTheSiteSaysToTheCache()
    {
        // The site's Cache-Control is the configuration. A rule set could keep an answer the site said not to keep.
        Assert.DoesNotContain("ruleSets", Bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("cacheBehavior", Bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("cacheDuration", Bicep, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/css")]
    [InlineData("text/plain")]
    [InlineData("application/rss+xml")]
    [InlineData("application/atom+xml")]
    [InlineData("application/xml")]
    [InlineData("application/json")]
    public void TheEdgeCompressesTheTextTheSiteSends(string contentType)
    {
        var listed = Regex.Match(Bicep, @"var compressedContentTypes = \[(?<types>[^\]]*)\]").Groups["types"].Value;

        Assert.Matches(@"compressionSettings:\s*\{\s*isCompressionEnabled:\s*true\s*contentTypesToCompress:\s*compressedContentTypes", Bicep);
        Assert.Contains($"'{contentType}'", listed, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEdgeDoesNotCompressWhatIsCompressedAlready()
    {
        var listed = Regex.Match(Bicep, @"var compressedContentTypes = \[(?<types>[^\]]*)\]").Groups["types"].Value;

        Assert.DoesNotMatch("image/(png|jpeg|gif|webp)|font/woff2|video/", listed);
    }

    [Fact]
    public void TheStackSaysWhichEndpointToEmpty()
    {
        Assert.Contains("output frontDoorEndpointId string = frontDoor ? endpoint!.id : ''", Bicep, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePurgeIsARequestTheAzureCliCanMakeWithoutAnExtension()
    {
        // "az afd" comes with the extension cdn in Azure CLI 2.90; "az resource invoke-action" is the CLI's own.
        Assert.Matches(@"'resource', 'invoke-action',\s*'--action', 'purge',\s*'--ids', \$endpointId,", Deploy);
        Assert.DoesNotMatch(@"(?m)^\s*(az\s+afd|'afd')", Deploy);
        // The script waits for the purge: readers must get the release before verify.ps1 passes.
        Assert.DoesNotContain("--no-wait", Deploy, StringComparison.Ordinal);
        Assert.Contains("contentPaths = @('/*')", Deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePurgeSpeaksTheApiVersionTheEndpointWasMadeWith()
    {
        var made = Regex.Match(Bicep, @"'Microsoft\.Cdn/profiles/afdEndpoints@(?<version>[\d-]+)'").Groups["version"].Value;

        Assert.NotEqual(string.Empty, made);
        Assert.Contains($"'--api-version', '{made}'", Deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCacheIsEmptiedAfterTheStackAndAfterEveryRegionAnswersAndOnlyBehindAFrontDoor()
    {
        var applied = Deploy.IndexOf("'stack', 'group', 'create'", StringComparison.Ordinal);
        var withoutFrontDoor = Deploy.IndexOf("if (-not $frontDoor) { exit 0 }", StringComparison.Ordinal);
        var regionsAsked = Deploy.IndexOf("& $testSite -BaseUrl ([string] $app.url) -Version $Version", StringComparison.Ordinal);
        var purged = Deploy.IndexOf("'resource', 'invoke-action'", StringComparison.Ordinal);

        Assert.True(applied >= 0 && applied < withoutFrontDoor && withoutFrontDoor < regionsAsked && regionsAsked < purged,
            $"Expected: apply the stack ({applied}), stop without a Front Door ({withoutFrontDoor}), ask the regions ({regionsAsked}), purge ({purged}).");
    }

    [Fact]
    public void TheVerificationTellsACachedAnswerFromTheSites()
    {
        Assert.Contains("Get-Header -Response $response -Name 'X-Cache'", TestSite, StringComparison.Ordinal);
        Assert.Contains("if ($cache -match 'HIT')", TestSite, StringComparison.Ordinal);
        Assert.Contains("Get-Header -Response $response -Name 'X-Release'", TestSite, StringComparison.Ordinal);
        Assert.Equal("X-Release", CacheHeadersMiddleware.ReleaseHeader);
    }

    [Fact]
    public void TheHealthVersionAndBuildAnswersSayNoStoreThemselves()
    {
        var endpoints = File.ReadAllText(Path.Join(Root, "src", "UI.Server", "Endpoints", "HealthEndpoints.cs"));

        Assert.Contains("headers.CacheControl = \"no-store\";", endpoints, StringComparison.Ordinal);
        Assert.Equal("no-store", CachePolicy.NoStore);
    }

    [Fact]
    public void TheCacheHeadersAreSetOutermostAndErrorsAreHandledInsideThem()
    {
        var program = File.ReadAllText(Path.Join(Root, "src", "UI.Server", "Program.cs"));
        var headers = program.IndexOf("app.UseMiddleware<CacheHeadersMiddleware>();", StringComparison.Ordinal);
        var errors = program.IndexOf("app.UseExceptionHandler(", StringComparison.Ordinal);
        var first = program.IndexOf("app.Use", StringComparison.Ordinal);

        Assert.Equal(first, headers);
        Assert.Contains("if (!app.Environment.IsDevelopment())", program[headers..errors], StringComparison.Ordinal);
        Assert.True(headers < errors, "An answer to a failed request gets its headers like any other.");
        Assert.True(errors < program.IndexOf("app.UseMiddleware<FrontDoorHostMiddleware>();", StringComparison.Ordinal));
    }

    /// <summary>A step against Azure is run once more, except after an error no attempt changes. The record lists them.</summary>
    [Fact]
    public void TheDecisionRecordListsTheErrorsThatAreNotTriedAgain()
    {
        var list = Regex.Match(Deploy, @"\$errorsNoAttemptChanges = @\((?<codes>[^)]*)\)").Groups["codes"].Value;
        var codes = Quoted().Matches(list).Select(match => match.Groups["code"].Value).ToList();

        Assert.Equal(9, codes.Count);
        Assert.All(codes, code => Assert.Contains($"`{code}`", Decision, StringComparison.Ordinal));
        Assert.Contains("RequestDisallowedByAzure", codes);
        Assert.Contains("InvalidTemplate", codes);
        // What the first production deployment failed with is tried again.
        Assert.DoesNotContain("InternalError", codes);
        Assert.DoesNotContain("DeploymentFailed", codes);
        Assert.Matches(@"\[int\] \$RetryPauseSeconds = 60\b", Deploy);
        Assert.Contains("once more after 60 seconds", Decision, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDecisionRecordStatesTheLifetimesTheSiteSends()
    {
        var browser = CachePolicy.Browser.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        var uploads = CachePolicy.BrowserUploads.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        var edge = CachePolicy.Edge.TotalSeconds.ToString(CultureInfo.InvariantCulture);

        Assert.Contains($"`public, max-age={browser}, s-maxage={edge}`", Decision, StringComparison.Ordinal);
        Assert.Contains($"`public, max-age={uploads}, s-maxage={edge}`", Decision, StringComparison.Ordinal);
        Assert.Contains($"`private, max-age={browser}`", Decision, StringComparison.Ordinal);
        Assert.Equal(5, CachePolicy.Browser.TotalMinutes);
        Assert.Contains("**The longest a reader sees an old page: five minutes after the purge is done.**", Decision, StringComparison.Ordinal);
        Assert.Equal(7, CachePolicy.Edge.TotalDays);
        Assert.Contains("for up to seven days", Deploy, StringComparison.Ordinal);
    }

    [GeneratedRegex("'(?<code>[A-Za-z]+)'")]
    private static partial Regex Quoted();
}
