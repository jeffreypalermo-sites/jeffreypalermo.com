namespace JeffreyPalermo.UI.Server;

/// <summary>
/// What a request says about the Azure Front Door it came through. Front Door sets <c>X-Azure-FDID</c> to the ID of
/// its profile, and a visitor cannot: the app's ingress is public, so that ID, compared with this site's own
/// (<see cref="SiteOptions.FrontDoorId"/>), is what tells a request of this site's Front Door from any other.
/// </summary>
public static class FrontDoorRequest
{
    private const string FrontDoorIdHeader = "X-Azure-FDID";
    private const string HealthProbeHeader = "X-FD-HealthProbe";

    /// <summary>True when the request carries the ID <paramref name="frontDoorId"/>; never when the site has none.</summary>
    public static bool IsFromThisSitesFrontDoor(HttpRequest request, string frontDoorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frontDoorId);
        return frontDoorId.Length > 0
            && string.Equals(request.Headers[FrontDoorIdHeader], frontDoorId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a Front Door marked the request as its health probe (<c>X-FD-HealthProbe: 1</c>). Anyone can send
    /// the header: it moves the sender's own request from one count to another and decides nothing else.
    /// </summary>
    public static bool IsAHealthProbe(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Headers.TryGetValue(HealthProbeHeader, out var probe) && probe == "1";
    }
}
