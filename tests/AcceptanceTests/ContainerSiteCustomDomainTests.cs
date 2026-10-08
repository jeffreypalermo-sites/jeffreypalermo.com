using System.Net;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// The container as it runs behind a Front Door with the custom domain (ADR-0014), over real HTTP: a second container
/// of the same image, given the one setting <c>deploy/infra/main.bicep</c> gives an app behind a Front Door, and
/// asked as the Front Door asks: by the container's own address, with the visitor's host forwarded.
/// </summary>
public sealed partial class ContainerSiteTests
{
    private const string FrontDoorId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task BehindAFrontDoorTheContainerServesTheCanonicalHostAndRedirectsTheOthers()
    {
        var container = await Command.RunAsync("docker", "run", "--detach", "--env", $"Site__FrontDoorId={FrontDoorId}", "--publish", $"127.0.0.1::{ContainerSite.ContainerPort}", site.Image);
        try
        {
            var published = await Command.RunAsync("docker", "port", container, $"{ContainerSite.ContainerPort}/tcp");
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://{published.Split('\n')[0].Trim()}/"), Timeout = TimeSpan.FromSeconds(30) };
            await WaitUntilReadyAsync(client);

            using var canonical = await AskAsync(client, "jeffreypalermo.com", "/2008/07/the-onion-architecture-part-1/");
            using var www = await AskAsync(client, "www.jeffreypalermo.com", "/2008/07/the-onion-architecture-part-1/?x=1");
            using var feeds = await AskAsync(client, "feeds.jeffreypalermo.com", "/jeffreypalermo");
            using var endpoint = await AskAsync(client, "jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net", "/2008/07/the-onion-architecture-part-1/");
            using var stranger = await AskAsync(client, "www.jeffreypalermo.com", "/2008/07/the-onion-architecture-part-1/", frontDoorId: "99999999-0000-0000-0000-000000000000");

            Assert.Equal(HttpStatusCode.OK, canonical.StatusCode);
            Assert.Equal("public, max-age=300, s-maxage=604800", Assert.Single(canonical.Headers.NonValidated["Cache-Control"]));
            Assert.Equal(HttpStatusCode.MovedPermanently, www.StatusCode);
            Assert.Equal("https://jeffreypalermo.com/2008/07/the-onion-architecture-part-1/?x=1", www.Headers.Location?.OriginalString);
            Assert.Equal("private, max-age=300", Assert.Single(www.Headers.NonValidated["Cache-Control"]));
            Assert.Equal(HttpStatusCode.MovedPermanently, feeds.StatusCode);
            Assert.Equal("https://jeffreypalermo.com/feed/", feeds.Headers.Location?.OriginalString);
            Assert.Equal("private, max-age=300", Assert.Single(feeds.Headers.NonValidated["Cache-Control"]));
            // The Front Door's own address keeps answering with the pages: verify.ps1 replays the contract through it.
            Assert.Equal(HttpStatusCode.OK, endpoint.StatusCode);
            // A forwarded host is believed from this site's Front Door only.
            Assert.Equal(HttpStatusCode.OK, stranger.StatusCode);
        }
        finally
        {
            await Command.RunAsync("docker", "rm", "--force", "--volumes", container);
        }
    }

    private static async Task<HttpResponseMessage> AskAsync(HttpClient client, string visitorHost, string path, string frontDoorId = FrontDoorId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", visitorHost);
        request.Headers.TryAddWithoutValidation("X-Azure-FDID", frontDoorId);
        return await client.SendAsync(request);
    }

    private static async Task WaitUntilReadyAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                using var ready = await client.GetAsync(new Uri("/_health/ready", UriKind.Relative));
                if (ready.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                // Not listening yet.
            }

            Assert.True(DateTime.UtcNow < deadline, "The second container did not become ready within 60 seconds.");
            await Task.Delay(250);
        }
    }
}
