using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JeffreyPalermo.Infrastructure.FrontMatter;

/// <summary>Reads and writes content files shaped as <c>---\n{yaml}\n---\n{body}</c>.</summary>
public static class FrontMatterDocument
{
    private const string Fence = "---";

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithEnumNamingConvention(LowerCaseNamingConvention.Instance)
        .WithTypeConverter(new IsoDateTimeConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithEnumNamingConvention(LowerCaseNamingConvention.Instance)
        .WithTypeConverter(new IsoDateTimeConverter())
        .WithTypeMapping<IReadOnlyList<string>, List<string>>()
        .Build();

    public static string Write<T>(T metadata, string body)
        where T : notnull
    {
        var builder = new StringBuilder();
        builder.Append(Fence).Append('\n');
        builder.Append(Serializer.Serialize(metadata).ReplaceLineEndings("\n"));
        builder.Append(Fence).Append('\n');
        builder.Append(body.ReplaceLineEndings("\n"));
        if (!body.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }

    public static (T Metadata, string Body) Read<T>(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.ReplaceLineEndings("\n");
        if (!normalized.StartsWith(Fence + "\n", StringComparison.Ordinal))
        {
            throw new FormatException("Content file must start with a '---' front matter fence.");
        }

        var close = normalized.IndexOf("\n" + Fence + "\n", Fence.Length, StringComparison.Ordinal);
        if (close < 0)
        {
            throw new FormatException("Front matter is missing its closing '---' fence.");
        }

        var yaml = normalized[(Fence.Length + 1)..(close + 1)];
        var body = normalized[(close + Fence.Length + 2)..];
        return (Deserializer.Deserialize<T>(yaml), body);
    }
}
