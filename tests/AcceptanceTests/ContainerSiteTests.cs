using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// The container image over real HTTP: what the kit's GitOps system deploys to every environment (ADR-0006). These
/// tests put the <c>Dockerfile</c> itself under test, including the content it ships beside the app.
/// </summary>
[Collection(FullSystem.Collection)]
public sealed partial class ContainerSiteTests(ContainerSite site, ITestOutputHelper output) : IClassFixture<ContainerSite>
{
    private const string LfsPointerHeader = "version https://git-lfs.github.com/spec/v1";

    [Fact]
    public async Task EveryLegacyUrlWorksInTheContainer()
    {
        using var client = site.Client();

        await UrlContractReplay.AssertEveryLegacyUrlWorksAsync(client, output, "in the container", site.LogAsync);
    }

    [Fact]
    public async Task ReadinessReportsTheVersionTheImageWasBuiltWith()
    {
        using var client = site.Client();

        var body = await client.GetStringAsync(new Uri("/_health/ready", UriKind.Relative));

        Assert.NotEqual(string.Empty, site.Version);
        Assert.Equal($"ready {site.Version}", body);
    }

    [Fact]
    public async Task TheImageRunsAsAnUnprivilegedUserOnPort8080()
    {
        var user = await site.InspectImageAsync("{{.Config.User}}");
        var ports = await site.InspectImageAsync("{{json .Config.ExposedPorts}}");

        Assert.NotEqual(string.Empty, user);
        Assert.DoesNotMatch(RootUser(), user);
        Assert.Contains($"\"{ContainerSite.ContainerPort}/tcp\"", ports, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/wp-content/uploads/2018/06/image257b0257d255b61255d1.png", "image/png")]
    [InlineData("/2008/07/the-onion-architecture-part-1/", "text/html")]
    [InlineData("/feed/", "application/rss+xml")]
    public async Task TheImageShipsTheContentBesideTheApp(string path, string mediaType)
    {
        using var client = site.Client();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>A checkout without Git LFS would bake a 133-byte pointer into the image where the video belongs.</summary>
    [Fact]
    public async Task LargeUploadsAreServedAsFilesNotGitLfsPointers()
    {
        var uploads = Path.Join(PublishedSite.RepositoryRoot, "content", "uploads");
        var tracked = LfsTrackedExtensions();
        var files = Directory.EnumerateFiles(uploads, "*", SearchOption.AllDirectories)
            .Where(file => tracked.Contains(Path.GetExtension(file)))
            .Select(file => Path.GetRelativePath(uploads, file).Replace('\\', '/'))
            .ToList();
        Assert.NotEmpty(files);
        using var client = site.Client();

        foreach (var file in files)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/wp-content/uploads/{file}", UriKind.Relative));
            request.Headers.Range = new RangeHeaderValue(0, LfsPointerHeader.Length - 1);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.True(response.Content.Headers.ContentRange?.Length > 1024, $"{file} is only {response.Content.Headers.ContentRange?.Length} bytes in the image.");
            Assert.NotEqual(LfsPointerHeader, Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync()));
        }
    }

    /// <summary>The nightly Verify environments workflow runs this script against tdd, uat and prod.</summary>
    [Fact]
    public async Task TheNightlyVerificationPassesAgainstTheContainer()
    {
        var environment = site.BaseAddress.GetLeftPart(UriPartial.Authority);

        var result = await Command.TryRunAsync("bash", environment: null, VerifyScript, environment);

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        Assert.Contains("0 violations", result.Output, StringComparison.Ordinal);
        Assert.Contains($"PASS {environment} (ready {site.Version})", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNightlyVerificationFailsForAnEnvironmentThatDoesNotAnswerAndStillChecksTheNext()
    {
        var silent = $"http://127.0.0.1:{PublishedSite.FreePort()}";
        var environment = site.BaseAddress.GetLeftPart(UriPartial.Authority);

        var result = await Command.TryRunAsync("bash", new Dictionary<string, string> { ["READY_TIMEOUT"] = "1" }, VerifyScript, silent, environment);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"FAIL {silent}: /_health/ready did not answer 200", result.Output, StringComparison.Ordinal);
        Assert.Contains($"PASS {environment}", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNightlyVerificationFailsForAnEnvironmentThatBreaksTheContract()
    {
        // A site that is ready and answers 404 to everything else: every preserved URL is a violation.
        var broken = $"http://127.0.0.1:{PublishedSite.FreePort()}";
        using var listener = new HttpListener();
        listener.Prefixes.Add($"{broken}/");
        listener.Start();
        using var stop = new CancellationTokenSource();
        var serving = ServeAsync(listener, stop.Token);

        var result = await Command.TryRunAsync("bash", environment: null, VerifyScript, broken);

        await stop.CancelAsync();
        listener.Stop();
        await serving;
        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"FAIL {broken} (ready broken): the URL contract is broken", result.Output, StringComparison.Ordinal);
    }

    private static string VerifyScript => Path.Join(PublishedSite.RepositoryRoot, "scripts", "verify-environments.sh");

    private static async Task ServeAsync(HttpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var ready = context.Request.Url?.AbsolutePath == "/_health/ready";
            context.Response.StatusCode = ready ? 200 : 404;
            await using (var body = context.Response.OutputStream)
            {
                await body.WriteAsync(Encoding.ASCII.GetBytes(ready ? "ready broken" : "not found"), stop);
            }
        }
    }

    /// <summary>The extensions <c>.gitattributes</c> stores with Git LFS under <c>content/uploads</c>.</summary>
    private static HashSet<string> LfsTrackedExtensions()
    {
        var attributes = File.ReadAllLines(Path.Join(PublishedSite.RepositoryRoot, ".gitattributes"));
        var extensions = attributes
            .Select(line => LfsPattern().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["extension"].Value);
        return new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^content/uploads/\*\*/\*(?<extension>\.\w+)\s.*\bfilter=lfs\b")]
    private static partial Regex LfsPattern();

    [GeneratedRegex(@"^(0|root)(:.*)?$")]
    private static partial Regex RootUser();
}
