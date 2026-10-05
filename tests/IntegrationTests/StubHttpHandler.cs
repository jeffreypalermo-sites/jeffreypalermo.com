using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>A stand-in for WordPress.com: answers by path+query and records every request.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public ConcurrentQueue<string> Requests { get; } = new();

    public static HttpResponseMessage Json(string json, int? totalPages = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (totalPages is { } pages)
        {
            response.Headers.Add("X-WP-TotalPages", pages.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return response;
    }

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.MovedPermanently)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!.PathAndQuery);
        return Task.FromResult(respond(request));
    }
}
