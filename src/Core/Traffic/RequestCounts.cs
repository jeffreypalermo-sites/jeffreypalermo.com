namespace JeffreyPalermo.Core.Traffic;

/// <summary>
/// What one instance answered in the last <see cref="RequestTally.WindowSeconds"/> seconds. Numbers only: nothing
/// of a request but its kind, whether it failed and how long it took is ever kept.
/// </summary>
/// <param name="FrontDoorTraffic">Readers' requests that came through this site's Front Door.</param>
/// <param name="DirectTraffic">Readers' requests that came to the instance's own address.</param>
/// <param name="Errors">Readers' requests answered with a status from 500 on. 404 and 410 are answers, not errors.</param>
/// <param name="P95Milliseconds">The time within which 95 of 100 readers' requests were answered; 0 when there were none.</param>
/// <param name="Checks">Checks of the running site.</param>
/// <param name="FrontDoorProbes">Health probes of a Front Door.</param>
public readonly record struct RequestCounts(long FrontDoorTraffic, long DirectTraffic, long Errors, int P95Milliseconds, long Checks, long FrontDoorProbes)
{
    /// <summary>Every reader's request, through the Front Door or not.</summary>
    public long Traffic => FrontDoorTraffic + DirectTraffic;
}
