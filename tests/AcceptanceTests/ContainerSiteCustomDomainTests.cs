using System.Net;
using System.Xml.Linq;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>
/// The container as it runs behind a Front Door with its own names (ADR-0014, ADR-0024), over real HTTP: a second
/// container of the same image, given the one setting <c>deploy/infra/main.bicep</c> gives an app behind a Front
/// Door, and asked as the Front Door asks: by the container's own address, with the visitor's host forwarded.
/// </summary>
public sealed partial class ContainerSiteTests
{
    private const string FrontDoorId = "11111111-2222-3333-4444-555555555555";

    private const string OnionPath = "/2008/07/the-onion-architecture-part-1/";

    /// <summary>
    /// The settings a move of jeffreypalermo.com would state (ADR-0024: prepared, not planned): that domain the
    /// canonical host, and its www. and feeds. the hosts the site is given to redirect. The image carries none of
    /// them, so the two rules by host are held over real HTTP in a container that is given them.
    /// </summary>
    private static readonly string[] TheHostsOfTheMove =
    [
        "Site__CanonicalHost=jeffreypalermo.com",
        "Site__HostsRedirectedToTheSameAddress__0=www.jeffreypalermo.com",
        "Site__HostsRedirectedToTheFeed__0=feeds.jeffreypalermo.com",
    ];

    /// <summary>
    /// The image as it is deployed: every name is answered with the pages and none with a redirect, and under every
    /// name the page, the feed, the sitemap and robots.txt name production's own name.
    /// </summary>
    [Fact]
    public async Task BehindAFrontDoorTheContainerServesEveryNameAndNamesTheCanonicalHost()
    {
        await WithASecondContainerAsync([$"Site__FrontDoorId={FrontDoorId}"], async client =>
        {
            foreach (var visitorHost in (string[])["www.jeffreypalermo.ceo", "uat.jeffreypalermo.ceo", "jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net", "www.www.jeffreypalermo.ceo", "feeds.www.jeffreypalermo.ceo", "feeds.jeffreypalermo.ceo", "www.jeffreypalermo.com", "feeds.jeffreypalermo.com"])
            {
                using var page = await AskAsync(client, visitorHost, OnionPath + "?x=1");
                using var feed = await AskAsync(client, visitorHost, "/feed/");
                using var sitemap = await AskAsync(client, visitorHost, "/wp-sitemap.xml");
                using var robots = await AskAsync(client, visitorHost, "/robots.txt");

                Assert.All((HttpResponseMessage[])[page, feed, sitemap, robots], response =>
                {
                    Assert.True(response.StatusCode == HttpStatusCode.OK, $"{visitorHost} was answered {(int)response.StatusCode} for {response.RequestMessage?.RequestUri}.");
                    Assert.Null(response.Headers.Location);
                    Assert.Equal("public, max-age=300, s-maxage=604800", Assert.Single(response.Headers.NonValidated["Cache-Control"]));
                });
                Assert.Contains($"<link rel=\"canonical\" href=\"https://www.jeffreypalermo.ceo{OnionPath}\"", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                var items = XDocument.Parse(await feed.Content.ReadAsStringAsync()).Descendants("item").ToList();
                Assert.Equal(10, items.Count);
                Assert.All(items, item =>
                {
                    Assert.StartsWith("https://www.jeffreypalermo.ceo/", item.Element("link")!.Value, StringComparison.Ordinal);
                    // What a reader knows the item by stays as it was first published: an identifier, not a link.
                    Assert.Equal("https://jeffreypalermo.com" + new Uri(item.Element("link")!.Value).AbsolutePath, item.Element("guid")!.Value);
                    Assert.Equal("false", item.Element("guid")!.Attribute("isPermaLink")?.Value);
                });
                Assert.Contains("<loc>https://www.jeffreypalermo.ceo/wp-sitemap-posts-post-1.xml</loc>", await sitemap.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                Assert.Contains("Sitemap: https://www.jeffreypalermo.ceo/wp-sitemap.xml\n", await robots.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        });
    }

    /// <summary>The container's own address, which the pipeline verifies through, names the canonical host too.</summary>
    [Fact]
    public async Task AtItsOwnAddressTheContainerNamesTheCanonicalHost()
    {
        using var client = site.Client();

        var page = await client.GetStringAsync(new Uri(OnionPath, UriKind.Relative));
        var robots = await client.GetStringAsync(new Uri("/robots.txt", UriKind.Relative));

        Assert.Contains($"<link rel=\"canonical\" href=\"https://www.jeffreypalermo.ceo{OnionPath}\"", page, StringComparison.Ordinal);
        Assert.Contains("Sitemap: https://www.jeffreypalermo.ceo/wp-sitemap.xml\n", robots, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenHostsToRedirectTheContainerServesTheCanonicalHostAndRedirectsTheOthers()
    {
        await WithASecondContainerAsync([$"Site__FrontDoorId={FrontDoorId}", .. TheHostsOfTheMove], async client =>
        {
            using var canonical = await AskAsync(client, "jeffreypalermo.com", "/2008/07/the-onion-architecture-part-1/");
            using var www = await AskAsync(client, "www.jeffreypalermo.com", "/2008/07/the-onion-architecture-part-1/?x=1");
            using var feeds = await AskAsync(client, "feeds.jeffreypalermo.com", "/jeffreypalermo");
            using var endpoint = await AskAsync(client, "jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net", "/2008/07/the-onion-architecture-part-1/");
            using var stranger = await AskAsync(client, "www.jeffreypalermo.com", "/2008/07/the-onion-architecture-part-1/", frontDoorId: "99999999-0000-0000-0000-000000000000");
            using var productionsOwn = await AskAsync(client, "www.jeffreypalermo.ceo", "/2008/07/the-onion-architecture-part-1/");
            using var uatsOwn = await AskAsync(client, "uat.jeffreypalermo.ceo", "/2008/07/the-onion-architecture-part-1/");

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
            // A name that is not given to redirect is answered with the pages and kept at the edge, whatever its
            // first label: www. of another domain is not a host the site was given.
            Assert.All((HttpResponseMessage[])[productionsOwn, uatsOwn], own =>
            {
                Assert.Equal(HttpStatusCode.OK, own.StatusCode);
                Assert.Equal("public, max-age=300, s-maxage=604800", Assert.Single(own.Headers.NonValidated["Cache-Control"]));
            });
        });
    }

    /// <summary>Runs a second container of the image with the given settings, and removes it afterwards.</summary>
    private async Task WithASecondContainerAsync(string[] settings, Func<HttpClient, Task> ask)
    {
        string[] arguments = ["run", "--detach", .. settings.SelectMany(setting => (string[])["--env", setting]), "--publish", $"127.0.0.1::{ContainerSite.ContainerPort}", site.Image];
        var container = await Command.RunAsync("docker", arguments);
        try
        {
            var published = await Command.RunAsync("docker", "port", container, $"{ContainerSite.ContainerPort}/tcp");
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://{published.Split('\n')[0].Trim()}/"), Timeout = TimeSpan.FromSeconds(30) };
            await WaitUntilReadyAsync(client);
            await ask(client);
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
