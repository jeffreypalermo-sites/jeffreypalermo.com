using System.Text.Json;

namespace JeffreyPalermo.UI.Server;

/// <summary>
/// The answer of <c>/_build</c>: what this release was built from and what its Build measured (ADR-0012). The Build
/// writes <c>build-facts.json</c> (<c>scripts/Write-BuildFacts.ps1</c>) and the container image carries it beside
/// the app. The file is believed only when it names the release this process reports: the facts of another build
/// are not this build's. Without such a file (a developer's machine, a test host) the answer is the one thing the
/// process knows itself, its version.
/// </summary>
public sealed class BuildFacts
{
    private BuildFacts(string json, bool measured)
    {
        Json = json;
        Measured = measured;
    }

    /// <summary>The JSON object the site answers with.</summary>
    public string Json { get; }

    /// <summary>True when the answer is the Build's file; false when it is the version alone.</summary>
    public bool Measured { get; }

    /// <summary>Reads the file at <paramref name="path"/>, when there is one, for the release <paramref name="version"/>.</summary>
    public static BuildFacts Load(string path, string version) => From(File.Exists(path) ? File.ReadAllText(path) : null, version);

    /// <summary>The answer for the release <paramref name="version"/>, given what the Build's file holds.</summary>
    public static BuildFacts From(string? file, string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return IsAbout(file, version)
            ? new BuildFacts(file!, measured: true)
            : new BuildFacts(JsonSerializer.Serialize(new Dictionary<string, string> { ["version"] = version }), measured: false);
    }

    private static bool IsAbout(string? file, string version)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(file);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var named)
                && named.ValueKind == JsonValueKind.String
                && named.GetString() == version;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
