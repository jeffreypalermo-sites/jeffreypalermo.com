namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>Writes one <c>{resource}.json</c> per public REST collection into the raw snapshot directory.</summary>
public sealed class SnapshotFetcher(WordPressApiClient client)
{
    public static readonly IReadOnlyList<string> Resources = ["posts", "pages", "comments", "media", "categories", "tags", "users"];

    /// <returns>Item count per resource; resources the site does not expose publicly are omitted.</returns>
    public async Task<IReadOnlyDictionary<string, int>> FetchAsync(string rawDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(rawDirectory);
        var counts = new Dictionary<string, int>();
        foreach (var resource in Resources)
        {
            var items = await client.GetAllAsync(resource, cancellationToken).ConfigureAwait(false);
            if (items is null)
            {
                continue;
            }

            await File.WriteAllTextAsync(
                Path.Join(rawDirectory, resource + ".json"),
                items.ToJsonString(WordPressConverter.JsonOptions).ReplaceLineEndings("\n") + "\n",
                cancellationToken).ConfigureAwait(false);
            counts[resource] = items.Count;
        }

        return counts;
    }
}
