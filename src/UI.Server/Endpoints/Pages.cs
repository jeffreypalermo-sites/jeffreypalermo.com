using JeffreyPalermo.UI.Server.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;

namespace JeffreyPalermo.UI.Server.Endpoints;

/// <summary>
/// Renders a page component as the answer to a request: plain HTML, rendered once on the server, with no client
/// runtime (ADR-0005). Routing stays with the minimal-API routes the URL contract proves (ADR-0009).
/// </summary>
internal static class Pages
{
    /// <summary>The name every page component gives the one parameter it is rendered from.</summary>
    private const string ModelParameter = "Model";

    public static IResult Render<TPage>(object model)
        where TPage : IComponent =>
        new WholePage(new RazorComponentResult<TPage>(new Dictionary<string, object?>(StringComparer.Ordinal) { [ModelParameter] = model }));

    /// <summary>The not-found page, with status 404.</summary>
    public static IResult NotFound() => new WholePage(new RazorComponentResult<NotFoundPage> { StatusCode = StatusCodes.Status404NotFound });

    /// <summary>
    /// A page rendered completely before the first byte is sent, so the answer carries its length. A component result
    /// by itself is sent in chunks with no length, and the Front Door does not compress such an answer (ADR-0013).
    /// </summary>
    private sealed class WholePage(IResult page) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            var body = response.Body;
            using var rendered = new MemoryStream();
            response.Body = rendered;
            try
            {
                await page.ExecuteAsync(httpContext).ConfigureAwait(false);
            }
            finally
            {
                response.Body = body;
            }

            response.ContentLength = rendered.Length;
            rendered.Position = 0;
            await rendered.CopyToAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
