using Microsoft.Extensions.Options;

namespace JeffreyPalermo.UI.Server;

/// <summary>
/// Behind Azure Front Door the request's Host is the container app's own address; the host the visitor asked for
/// arrives in <c>X-Forwarded-Host</c>. This restores it, so the legacy URL rules see <c>www.</c> and <c>feeds.</c> as
/// the visitor typed them. The header is believed only when the request carries this site's Front Door ID
/// (<c>X-Azure-FDID</c>, which Front Door sets and a visitor cannot): anyone can send a forwarded host.
/// </summary>
public sealed class FrontDoorHostMiddleware(RequestDelegate next, IOptions<SiteOptions> options)
{
    private const string FrontDoorIdHeader = "X-Azure-FDID";
    private const string ForwardedHostHeader = "X-Forwarded-Host";

    private readonly string _frontDoorId = options.Value.FrontDoorId;

    public Task InvokeAsync(HttpContext context)
    {
        if (_frontDoorId.Length > 0
            && string.Equals(context.Request.Headers[FrontDoorIdHeader], _frontDoorId, StringComparison.OrdinalIgnoreCase)
            && context.Request.Headers[ForwardedHostHeader].ToString() is { Length: > 0 } forwarded
            && HostString.FromUriComponent(forwarded) is { HasValue: true } host
            && Uri.CheckHostName(host.Host) != UriHostNameType.Unknown)
        {
            context.Request.Host = host;
        }

        return next(context);
    }
}
