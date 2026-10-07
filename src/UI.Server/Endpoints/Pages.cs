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

    public static RazorComponentResult<TPage> Render<TPage>(object model)
        where TPage : IComponent =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal) { [ModelParameter] = model });

    /// <summary>The not-found page, with status 404.</summary>
    public static RazorComponentResult<NotFoundPage> NotFound() => new() { StatusCode = StatusCodes.Status404NotFound };
}
