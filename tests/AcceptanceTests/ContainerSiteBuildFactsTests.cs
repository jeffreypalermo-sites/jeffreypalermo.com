using System.Net;
using System.Text.Json;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// What the image says about its build at <c>/_build</c> (ADR-0012). The system's check asks every environment the
/// same (CAP-079 of the demo-environment-kit): the answer from any origin, the version that is deployed, the commit,
/// and the count of the lines of code.
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
}
