using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using JeffreyPalermo.Infrastructure.Urls;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// What the image says about its build at <c>/_build</c> (ADR-0012). The system's check asks every environment the
/// same (CAP-079 of the demo-environment-kit): the answer from any origin, the version that is deployed, the commit,
/// and the count of the lines of code. The fleet reads two more things from the facts of every production
/// application (FLEET-014): tests at three levels, and static analysis.
/// </summary>
public sealed partial class ContainerSiteTests
{
    [Fact]
    public async Task TheImageSaysWhatItWasBuiltFromToAnyOrigin()
    {
        using var client = site.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/_build", UriKind.Relative));
        request.Headers.Add("Origin", "https://capability-check.example");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var facts = JsonDocument.Parse(body);
        var root = facts.RootElement;
        Assert.Equal(site.Version, root.GetProperty("version").GetString());
        Assert.Matches("^[0-9a-f]{40}$", root.GetProperty("commit").GetString());
        Assert.True(root.GetProperty("code").GetProperty("linesOfCode").GetInt64() > 0, body);
        Assert.Equal("C#", root.GetProperty("code").GetProperty("languages")[0].GetProperty("name").GetString());
        Assert.True(root.GetProperty("builtAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow.AddMinutes(1), body);
    }

    [Fact]
    public async Task TheFactsNameTheReleaseTheImageReports()
    {
        using var client = site.Client();

        using var build = JsonDocument.Parse(await client.GetStringAsync(new Uri("/_build", UriKind.Relative)));
        using var version = JsonDocument.Parse(await client.GetStringAsync(new Uri("/_version", UriKind.Relative)));
        var ready = await client.GetStringAsync(new Uri("/_health/ready", UriKind.Relative));

        Assert.Equal($"ready {build.RootElement.GetProperty("version").GetString()}", ready);
        Assert.Equal(version.RootElement.GetProperty("version").GetString(), build.RootElement.GetProperty("version").GetString());
    }

    /// <summary>
    /// As the fleet reads it: <c>tests.unit</c>, <c>tests.integration</c> and <c>tests.acceptance</c> are each a
    /// number above zero. Acceptance is declared, and the facts say so: the URLs of the contract, which the pipeline
    /// replays against the first environment after the Build. Nothing that has not run is counted as passed.
    /// </summary>
    [Fact]
    public async Task TheImageCountsItsTestsAtThreeLevels()
    {
        using var client = site.Client();

        var body = await client.GetStringAsync(new Uri("/_build", UriKind.Relative));

        using var facts = JsonDocument.Parse(body);
        var tests = facts.RootElement.GetProperty("tests");
        Assert.All((string[])["unit", "integration", "acceptance"], level => Assert.True(tests.GetProperty(level).GetInt32() > 0, $"{level}: {body}"));

        var contract = UrlContractFile.Read(await File.ReadAllTextAsync(Path.Join(PublishedSite.RepositoryRoot, "tests", "contract", "url-contract.tsv")));
        Assert.Equal(contract.Count, tests.GetProperty("acceptance").GetInt32());
        Assert.Equal("declared", tests.GetProperty("acceptanceIs").GetProperty("kind").GetString());
        Assert.Contains("tests/contract/url-contract.tsv", tests.GetProperty("acceptanceIs").GetProperty("counted").GetString(), StringComparison.Ordinal);

        // The results the image carries are of the tests that ended before it was built. These tests, the
        // full-system tests, run against it: their count cannot be in it.
        Assert.Equal(tests.GetProperty("unit").GetInt32() + tests.GetProperty("integration").GetInt32(), tests.GetProperty("passed").GetInt32());
        Assert.Equal(0, tests.GetProperty("failed").GetInt32());
        Assert.Equal(JsonValueKind.Null, tests.GetProperty("fullSystem").ValueKind);
    }

    /// <summary>
    /// As the fleet reads it: <c>analysis</c> is there. It names the analyzers the compile runs and how the build is
    /// set (<c>Directory.Build.props</c>), what the compile found, and what the code switches off. A warning is an
    /// error, so the compile of an image that exists found nothing.
    /// </summary>
    [Fact]
    public async Task TheImageReportsItsStaticAnalysis()
    {
        using var client = site.Client();

        var body = await client.GetStringAsync(new Uri("/_build", UriKind.Relative));

        using var facts = JsonDocument.Parse(body);
        var analysis = facts.RootElement.GetProperty("analysis");
        Assert.True(analysis.ValueKind == JsonValueKind.Object, body);
        var set = XDocument.Load(Path.Join(PublishedSite.RepositoryRoot, "Directory.Build.props"));
        Assert.Equal(".NET analyzers", analysis.GetProperty("tool").GetString());
        Assert.Equal(set.Descendants("AnalysisLevel").Single().Value, analysis.GetProperty("analysisLevel").GetString());
        Assert.Equal("true", set.Descendants("TreatWarningsAsErrors").Single().Value);
        Assert.True(analysis.GetProperty("warningsAsErrors").GetBoolean(), body);
        Assert.Equal(0, analysis.GetProperty("problems").GetInt32());
        Assert.True(analysis.GetProperty("projects").GetInt32() > 0, body);
        Assert.Equal(analysis.GetProperty("suppressed").EnumerateObject().Sum(kind => kind.Value.GetInt32()), analysis.GetProperty("suppressions").GetInt32());
    }
}
