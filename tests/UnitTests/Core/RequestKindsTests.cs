using JeffreyPalermo.Core.Traffic;

namespace JeffreyPalermo.UnitTests.Core;

/// <summary>Which requests are checks of the running instance and which are traffic (ADR-0025).</summary>
public class RequestKindsTests
{
    [Theory]
    [InlineData("/_health/live")]
    [InlineData("/_health/ready")]
    [InlineData("/_health/no-such-check")]
    [InlineData("/_version")]
    [InlineData("/_version/")]
    [InlineData("/_build")]
    [InlineData("/_telemetry")]
    [InlineData("/_telemetry/")]
    public void AnAddressAboutTheRunningSiteIsACheckHoweverItCame(string path)
    {
        Assert.True(RequestKinds.IsAboutTheRunningSite(path));
        Assert.Equal(RequestKind.Check, RequestKinds.Of(path, fromThisSitesFrontDoor: false, frontDoorHealthProbe: false));
        Assert.Equal(RequestKind.Check, RequestKinds.Of(path, fromThisSitesFrontDoor: true, frontDoorHealthProbe: false));
    }

    /// <summary>Pages, feeds, sitemaps, the site's own files, uploads, redirects, and addresses that lead nowhere.</summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/2008/07/the-onion-architecture-part-1/")]
    [InlineData("/feed/")]
    [InlineData("/wp-sitemap.xml")]
    [InlineData("/robots.txt")]
    [InlineData("/favicon.ico")]
    [InlineData("/_assets/site.css")]
    [InlineData("/wp-content/uploads/2018/06/a.png")]
    [InlineData("/wp-admin/")]
    [InlineData("/no-such-page-anywhere-at-all/")]
    [InlineData("/_telemetry-of-something-else")]
    [InlineData("/_versions/")]
    [InlineData("/_buildings/")]
    [InlineData("/_healthy/")]
    [InlineData("/_TELEMETRY")]
    [InlineData("/telemetry")]
    public void EverythingElseIsTrafficThroughTheFrontDoorOrDirect(string path)
    {
        Assert.False(RequestKinds.IsAboutTheRunningSite(path));
        Assert.Equal(RequestKind.FrontDoorTraffic, RequestKinds.Of(path, fromThisSitesFrontDoor: true, frontDoorHealthProbe: false));
        Assert.Equal(RequestKind.DirectTraffic, RequestKinds.Of(path, fromThisSitesFrontDoor: false, frontDoorHealthProbe: false));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/", false)]
    [InlineData("/_health/ready", true)]
    [InlineData("/2008/07/the-onion-architecture-part-1/", false)]
    public void AFrontDoorsHealthProbeIsNeitherTrafficNorACheckOfTheDashboard(string path, bool fromThisSitesFrontDoor) =>
        Assert.Equal(RequestKind.FrontDoorProbe, RequestKinds.Of(path, fromThisSitesFrontDoor, frontDoorHealthProbe: true));
}
