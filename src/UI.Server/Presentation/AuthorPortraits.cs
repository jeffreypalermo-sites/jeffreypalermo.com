using System.Collections.Concurrent;

namespace JeffreyPalermo.UI.Server.Presentation;

/// <summary>
/// The pictures beside an author's name: files the site serves itself, <c>wwwroot/_assets/authors/{slug}.jpg</c>, where
/// WordPress asked Gravatar. An author without a file has no picture.
/// </summary>
public sealed class AuthorPortraits(IWebHostEnvironment environment)
{
    private readonly ConcurrentDictionary<string, string?> _paths = new(StringComparer.Ordinal);

    /// <summary>The small picture shown beside each post, or null.</summary>
    public string? Avatar(string authorSlug) => Find($"{authorSlug}.jpg");

    /// <summary>The large picture of the sidebar's profile, or null.</summary>
    public string? Profile(string authorSlug) => Find($"{authorSlug}-profile.jpg");

    private string? Find(string fileName) => _paths.GetOrAdd(fileName, name =>
        environment.WebRootFileProvider.GetFileInfo($"_assets/authors/{name}").Exists ? $"{SiteUrls.Assets}authors/{name}" : null);
}
