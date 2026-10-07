using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>/_build</c> in-process (ADR-0012): the site answers the file the Build wrote about its release, and its
/// version alone where there is no such file. The system's health dashboard reads the answer from another origin.
/// </summary>
public sealed class BuildFactsEndpointTests(SiteFactory factory) : IClassFixture<SiteFactory>, IDisposable
{
    private const string Measured = """
        {
          "version": "1.0.41",
          "commit": "0a1b2c3d4e5f60718293a4b5c6d7e8f901234567",
          "code": { "linesOfCode": 10184, "files": 146, "languages": [ { "name": "C#", "lines": 7549, "files": 103 } ] },
          "tests": { "unit": 384, "integration": 156, "acceptance": null },
          "analysis": null
        }
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("jpcom-build-facts-endpoint-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task WithoutTheBuildsFileTheSiteAnswersItsVersion()
    {
        using var response = await factory.ClientFor().GetAsync(new Uri("/_build", UriKind.Relative));
        var version = await factory.ClientFor().GetStringAsync(new Uri("/_version", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(version, await response.Content.ReadAsStringAsync());
        Assert.Equal("""{"version":"dev"}""", version);
    }

    [Fact]
    public async Task TheBuildsFileIsAnsweredAsItWasWritten()
    {
        using var site = SiteOf(release: "1.0.41", Measured);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/_build", UriKind.Relative));
        request.Headers.Add("Origin", "https://dashboard.example");

        using var response = await site.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(Measured, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheFactsNameTheReleaseTheSiteReports()
    {
        using var site = SiteOf(release: "1.0.41", Measured);
        var client = site.CreateClient();

        using var build = JsonDocument.Parse(await client.GetStringAsync(new Uri("/_build", UriKind.Relative)));
        using var version = JsonDocument.Parse(await client.GetStringAsync(new Uri("/_version", UriKind.Relative)));
        var ready = await client.GetStringAsync(new Uri("/_health/ready", UriKind.Relative));

        Assert.Equal("1.0.41", build.RootElement.GetProperty("version").GetString());
        Assert.Equal("1.0.41", version.RootElement.GetProperty("version").GetString());
        Assert.Equal("ready 1.0.41", ready);
    }

    [Theory]
    [InlineData(Measured, "the facts of another release")]
    [InlineData("<html>not the facts</html>", "a file that is not JSON")]
    [InlineData("", "an empty file")]
    public async Task AFileThatIsNotAboutThisReleaseIsNotAnswered(string file, string what)
    {
        using var site = SiteOf(release: "1.0.42", file);

        var answer = await site.CreateClient().GetStringAsync(new Uri("/_build", UriKind.Relative));

        Assert.True(answer == """{"version":"1.0.42"}""", $"With {what} the site answered: {answer}");
    }

    /// <summary>The site as release <paramref name="release"/>, with <paramref name="file"/> where the image carries the Build's facts.</summary>
    private WebApplicationFactory<Program> SiteOf(string release, string file)
    {
        var path = Path.Join(_folder, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, file);
        return factory.WithWebHostBuilder(builder => builder.UseSetting("Site:Version", release).UseSetting("Site:BuildFactsPath", path));
    }
}
