using System.Net;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// Full-system fixture: the container image as it is delivered (ADR-0006). It builds the repository's
/// <c>Dockerfile</c> as the Build workflow does, the build's facts first (ADR-0012), and runs the image with
/// <c>docker run</c>, with no settings beyond the image's own. The Build workflow names the image it already built
/// in <see cref="ImageVariable"/>, so the image that passes these tests is the image that is released. Needs Docker;
/// no third-party systems are involved.
/// </summary>
public sealed class ContainerSite : IAsyncLifetime
{
    /// <summary>Environment variable naming an image to test instead of building one.</summary>
    public const string ImageVariable = "JPCOM_IMAGE";

    /// <summary>The port the image listens on, which the kit's deployable is configured with.</summary>
    public const int ContainerPort = 8080;

    /// <summary>
    /// What <c>build.yml</c> requires of the facts before it builds the image, as <c>-File</c> passes a list. Not the
    /// coverage: these tests are not the Build, and what stands in for its results holds none.
    /// </summary>
    private const string RequiredOfTheFacts = "tests.unit,tests.integration,tests.acceptance,analysis";

    private string? _builtImage;
    private string? _container;

    public string Image { get; private set; } = string.Empty;

    /// <summary>The version the image was built with: build argument <c>VERSION</c>, which the Build workflow also writes as a label.</summary>
    public string Version { get; private set; } = string.Empty;

    public Uri BaseAddress { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var given = Environment.GetEnvironmentVariable(ImageVariable);
        if (string.IsNullOrWhiteSpace(given))
        {
            Version = "acceptance";
            _builtImage = $"jpcom-acceptance:{Guid.NewGuid():N}";
            await BuildAsync(_builtImage, Version);
            Image = _builtImage;
        }
        else
        {
            Image = given;
            Version = await Command.RunAsync("docker", "image", "inspect", "--format", "{{ index .Config.Labels \"org.opencontainers.image.version\" }}", Image);
        }

        _container = await Command.RunAsync("docker", "run", "--detach", "--publish", $"127.0.0.1::{ContainerPort}", Image);
        var published = await Command.RunAsync("docker", "port", _container, $"{ContainerPort}/tcp");
        BaseAddress = new Uri($"http://{published.Split('\n')[0].Trim()}/");

        using var http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await Command.RunAsync("docker", "inspect", "--format", "{{.State.Running}}", _container) != "true")
            {
                throw new InvalidOperationException($"The container stopped:\n{await LogAsync()}");
            }

            try
            {
                using var ready = await http.GetAsync(new Uri("/_health/ready", UriKind.Relative));
                if (ready.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException)
            {
                // Still starting.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"The container did not become ready within 60 seconds:\n{await LogAsync()}");
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await Command.RunAsync("docker", "rm", "--force", "--volumes", _container);
        }

        if (_builtImage is not null)
        {
            await Command.RunAsync("docker", "image", "rm", "--force", _builtImage);
        }
    }

    /// <summary>
    /// The two steps of the Build workflow that make the image: <c>scripts/Write-BuildFacts.ps1</c> writes
    /// <c>build-facts.json</c> beside the <c>Dockerfile</c>, which copies it into the image. The script runs with
    /// what the Build requires of it, so facts that lack a level of tests or the analysis build no image here
    /// either. These tests are not the Build: what its first job hands over is stood in for
    /// (<see cref="StandInResults"/>). The version, the commit, the code and the acceptance checks the release
    /// declares are this repository's own. The file is a build output and is removed again.
    /// </summary>
    private static async Task BuildAsync(string image, string version)
    {
        var root = PublishedSite.RepositoryRoot;
        var facts = Path.Join(root, "build-facts.json");
        var results = Directory.CreateTempSubdirectory("jpcom-stand-in-results-").FullName;
        try
        {
            StandInResults(results);
            await Command.RunAsync(
                "pwsh", "-NoProfile", "-File", Path.Join(root, "scripts", "Write-BuildFacts.ps1"),
                "-Version", version, "-ResultsPath", results, "-Require", RequiredOfTheFacts, "-OutputPath", facts);
            await Command.RunAsync("docker", "build", "--build-arg", $"VERSION={version}", "--tag", image, root);
        }
        finally
        {
            File.Delete(facts);
            Directory.Delete(results, recursive: true);
        }
    }

    /// <summary>
    /// What job <c>test</c> of the Build hands to job <c>image</c>, stood in for: the results of one unit test and of
    /// one integration test that passed, and the log of a compile that found nothing.
    /// </summary>
    private static void StandInResults(string folder)
    {
        foreach (var layer in (string[])["UnitTests", "IntegrationTests"])
        {
            File.WriteAllText(Path.Join(folder, $"{layer}.trx"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun id="1" name="stand-in" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testId="1" testName="StandIn" outcome="Passed" />
                  </Results>
                  <TestDefinitions>
                    <UnitTest name="StandIn" storage="/stand-in/tests/{layer}/bin/release/net10.0/jeffreypalermo.{layer.ToLowerInvariant()}.dll" id="1" />
                  </TestDefinitions>
                </TestRun>
                """);
        }

        File.WriteAllText(Path.Join(folder, "compile.msbuild.log"), """
              JeffreyPalermo.UI.Server -> /stand-in/src/UI.Server/bin/Release/net10.0/JeffreyPalermo.UI.Server.dll

            Build succeeded.
                0 Warning(s)
                0 Error(s)

            Time Elapsed 00:00:01.00
            """);
    }

    /// <summary>A client that does not follow redirects.</summary>
    public HttpClient Client() =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>What the app wrote to the container's output.</summary>
    public Task<string> LogAsync() =>
        _container is null ? Task.FromResult(string.Empty) : Command.RunAsync("docker", "logs", _container);

    /// <summary>One value of the image's configuration, as a <c>docker image inspect</c> format.</summary>
    public Task<string> InspectImageAsync(string format) => Command.RunAsync("docker", "image", "inspect", "--format", format, Image);
}
