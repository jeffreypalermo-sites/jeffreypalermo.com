using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>Reads every item of a public WordPress REST collection, following <c>X-WP-TotalPages</c>.</summary>
public sealed class WordPressApiClient(HttpClient http)
{
    private const int PageSize = 100;

    /// <returns>All items in id order, or <c>null</c> when the site does not expose the collection publicly.</returns>
    public async Task<JsonArray?> GetAllAsync(string resource, CancellationToken cancellationToken = default)
    {
        var items = new JsonArray();
        var page = 1;
        int totalPages;
        do
        {
            using var response = await http.GetAsync(
                new Uri($"wp-json/wp/v2/{resource}?per_page={PageSize}&page={page}&orderby=id&order=asc", UriKind.Relative),
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            totalPages = response.Headers.TryGetValues("X-WP-TotalPages", out var values)
                ? int.Parse(values.First(), CultureInfo.InvariantCulture)
                : 1;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var pageItems = JsonNode.Parse(json)?.AsArray()
                ?? throw new InvalidDataException($"Expected a JSON array from '{resource}' page {page}.");

            foreach (var item in pageItems.ToList())
            {
                pageItems.Remove(item);
                if (item is JsonObject obj)
                {
                    obj.Remove("_links");
                }

                items.Add(item);
            }

            page++;
        }
        while (page <= totalPages);

        return items;
    }
}
