using System.Net;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// Full-system fixture: the container image as it is delivered (ADR-0006). It builds the repository's
/// <c>Dockerfile</c> and runs the image with <c>docker run</c>, with no settings beyond the image's own. The Build
/// workflow names the image it already built in <see cref="ImageVariable"/>, so the image that passes these tests is
/// the image that is released. Needs Docker; no third-party systems are involved.
/// </summary>
public sealed class ContainerSite : IAsyncLifetime
{
    /// <summary>Environment variable naming an image to test instead of building one.</summary>
    public const string ImageVariable = "JPCOM_IMAGE";

    /// <summary>The port the image listens on, which the kit's deployable is configured with.</summary>
    public const int ContainerPort = 8080;

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
            await Command.RunAsync("docker", "build", "--build-arg", $"VERSION={Version}", "--tag", _builtImage, PublishedSite.RepositoryRoot);
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

    /// <summary>A client that does not follow redirects.</summary>
    public HttpClient Client() =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>What the app wrote to the container's output.</summary>
    public Task<string> LogAsync() =>
        _container is null ? Task.FromResult(string.Empty) : Command.RunAsync("docker", "logs", _container);

    /// <summary>One value of the image's configuration, as a <c>docker image inspect</c> format.</summary>
    public Task<string> InspectImageAsync(string format) => Command.RunAsync("docker", "image", "inspect", "--format", format, Image);
}
