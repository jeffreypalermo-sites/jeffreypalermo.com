using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The site behind Azure Front Door: the app is addressed by its own origin host, and the host the visitor asked for
/// arrives in <c>X-Forwarded-Host</c>, believed only with this site's Front Door ID.
/// </summary>
public sealed class FrontDoorHostTests : IClassFixture<SiteFactory>
{
    private const string FrontDoorId = "11111111-2222-3333-4444-555555555555";
    private const string OriginHost = "ca-jpcom-prod-web-eus2.example.azurecontainerapps.io";

    private readonly WebApplicationFactory<Program> _behindFrontDoor;

    public FrontDoorHostTests(SiteFactory factory) =>
        _behindFrontDoor = factory.WithWebHostBuilder(builder => builder.UseSetting("Site:FrontDoorId", FrontDoorId));

    [Theory]
    [InlineData("www.jeffreypalermo.com", "/2008/07/the-onion-architecture-part-1/", "https://jeffreypalermo.com/2008/07/the-onion-architecture-part-1/")]
    [InlineData("feeds.jeffreypalermo.com", "/jeffreypalermo", "https://jeffreypalermo.com/feed/")]
    public async Task TheVisitorsHostDecidesWhenFrontDoorForwardsIt(string visitorHost, string path, string location)
    {
        using var response = await SendAsync(path, visitorHost, FrontDoorId);

        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task TheCanonicalHostThroughFrontDoorIsServed()
    {
        using var response = await SendAsync("/2008/07/the-onion-architecture-part-1/", "jeffreypalermo.com", FrontDoorId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("99999999-0000-0000-0000-000000000000")]
    public async Task AForwardedHostWithoutThisFrontDoorsIdIsIgnored(string? frontDoorId)
    {
        // Anyone can send X-Forwarded-Host to the origin directly: without the ID it must not turn into a redirect.
        using var response = await SendAsync("/2008/07/the-onion-architecture-part-1/", "www.jeffreypalermo.com", frontDoorId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AMalformedForwardedHostIsIgnored()
    {
        using var response = await SendAsync("/2008/07/the-onion-architecture-part-1/", "not a host/..", FrontDoorId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithoutAFrontDoorNoForwardedHostIsBelieved()
    {
        using var plain = new SiteFactory();
        using var client = plain.ClientFor(OriginHost);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/2008/07/the-onion-architecture-part-1/", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "www.jeffreypalermo.com");
        request.Headers.TryAddWithoutValidation("X-Azure-FDID", FrontDoorId);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string forwardedHost, string? frontDoorId)
    {
        var client = _behindFrontDoor.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri($"https://{OriginHost}/") });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost);
        if (frontDoorId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Azure-FDID", frontDoorId);
        }

        return await client.SendAsync(request);
    }
}
