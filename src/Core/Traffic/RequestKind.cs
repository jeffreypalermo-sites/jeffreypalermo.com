namespace JeffreyPalermo.Core.Traffic;

/// <summary>What a request is to the count a running instance keeps of itself (ADR-0025).</summary>
public enum RequestKind
{
    /// <summary>A reader's request that came through this site's Front Door.</summary>
    FrontDoorTraffic,

    /// <summary>A reader's request that came to the instance's own address.</summary>
    DirectTraffic,

    /// <summary>A check of the running site: its health, version, build facts or these counts.</summary>
    Check,

    /// <summary>A health probe of an Azure Front Door.</summary>
    FrontDoorProbe,
}

/// <summary>
/// The rule that tells the checks of a running instance from its traffic. Every request is one or the other: a
/// check asks about the instance, traffic asks for something the site publishes, whatever the answer is (a page, a
/// feed, a file, a redirect, "not found" or "gone").
/// </summary>
public static class RequestKinds
{
    /// <summary>
    /// True for <c>/_health/…</c>, <c>/_version</c>, <c>/_build</c> and <c>/_telemetry</c>: the addresses that
    /// answer about the running instance. Not the site's own files under <c>/_assets/</c>, which a reader's page
    /// loads. The comparison is exact: the legacy URL rules pass every <c>/_…</c> path through as it was sent.
    /// </summary>
    public static bool IsAboutTheRunningSite(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var first = path.AsSpan().TrimStart('/');
        var end = first.IndexOf('/');
        first = end < 0 ? first : first[..end];
        return first is "_health" or "_version" or "_build" or "_telemetry";
    }

    /// <summary>The kind of a request.</summary>
    /// <param name="path">The path as it was asked for.</param>
    /// <param name="fromThisSitesFrontDoor">True when the request carries this site's own Front Door ID.</param>
    /// <param name="frontDoorHealthProbe">True when a Front Door marked the request as its health probe.</param>
    public static RequestKind Of(string path, bool fromThisSitesFrontDoor, bool frontDoorHealthProbe)
    {
        if (frontDoorHealthProbe)
        {
            return RequestKind.FrontDoorProbe;
        }

        if (IsAboutTheRunningSite(path))
        {
            return RequestKind.Check;
        }

        return fromThisSitesFrontDoor ? RequestKind.FrontDoorTraffic : RequestKind.DirectTraffic;
    }
}
