using JeffreyPalermo.UI.Server;
using Microsoft.AspNetCore.Http;

namespace JeffreyPalermo.UnitTests.UI;

/// <summary>
/// What a request says about the Front Door it came through (ADR-0008, ADR-0025): the one rule that believes a
/// forwarded host and counts a request as the Front Door's.
/// </summary>
public class FrontDoorRequestTests
{
    private const string FrontDoorId = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData(FrontDoorId, true)]
    [InlineData("11111111-2222-3333-4444-55555555555A", false)]
    [InlineData("99999999-0000-0000-0000-000000000000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyThisSitesOwnIdIsBelieved(string? sent, bool believed) =>
        Assert.Equal(believed, FrontDoorRequest.IsFromThisSitesFrontDoor(RequestWith("X-Azure-FDID", sent), FrontDoorId));

    [Fact]
    public void TheIdIsComparedWithoutRegardToCase() =>
        Assert.True(FrontDoorRequest.IsFromThisSitesFrontDoor(RequestWith("X-Azure-FDID", "0A1B2C3D-4E5F-6789-ABCD-EF0123456789"), "0a1b2c3d-4e5f-6789-abcd-ef0123456789"));

    /// <summary>A site that has no Front Door (tdd) believes no request, also not one that sends the header empty.</summary>
    [Theory]
    [InlineData(FrontDoorId)]
    [InlineData("")]
    [InlineData(null)]
    public void ASiteWithoutAFrontDoorBelievesNoRequest(string? sent) =>
        Assert.False(FrontDoorRequest.IsFromThisSitesFrontDoor(RequestWith("X-Azure-FDID", sent), string.Empty));

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AHealthProbeIsMarkedWithOne(string? sent, bool probe) =>
        Assert.Equal(probe, FrontDoorRequest.IsAHealthProbe(RequestWith("X-FD-HealthProbe", sent)));

    private static HttpRequest RequestWith(string header, string? value)
    {
        var context = new DefaultHttpContext();
        if (value is not null)
        {
            context.Request.Headers[header] = value;
        }

        return context.Request;
    }
}
