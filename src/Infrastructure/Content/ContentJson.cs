using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeffreyPalermo.Infrastructure.Content;

/// <summary>JSON shape of the archive files under <c>content/</c>: snake_case, indented, readable non-ASCII.</summary>
public static class ContentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
