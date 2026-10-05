using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace JeffreyPalermo.Infrastructure.FrontMatter;

/// <summary>
/// Writes DateTime as <c>yyyy-MM-ddTHH:mm:ss</c> (with a trailing <c>Z</c> for UTC) without time-zone conversion.
/// YamlDotNet's built-in converter shifts unspecified-kind values, which would move post dates.
/// </summary>
internal sealed class IsoDateTimeConverter : IYamlTypeConverter
{
    private const string LocalFormat = "yyyy-MM-dd'T'HH:mm:ss";

    public bool Accepts(Type type) => type == typeof(DateTime) || type == typeof(DateTime?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        if (type == typeof(DateTime?) && (scalar.Value.Length == 0 || scalar.Value == "~" || scalar.Value == "null"))
        {
            return null;
        }

        return scalar.Value.EndsWith('Z')
            ? DateTime.Parse(scalar.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            : DateTime.ParseExact(scalar.Value, LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var dateTime = (DateTime)value!;
        var text = dateTime.ToString(LocalFormat, CultureInfo.InvariantCulture);
        if (dateTime.Kind == DateTimeKind.Utc)
        {
            text += "Z";
        }

        emitter.Emit(new Scalar(text));
    }
}
