using System.Globalization;
using System.Text;
using JeffreyPalermo.Core.Urls;

namespace JeffreyPalermo.Infrastructure.Urls;

/// <summary>Tab-separated, one entry per line, sorted by URL so diffs stay readable.</summary>
public static class UrlContractFile
{
    private const string Header = "url\tclass\tstatus\tlocation\tfinal_status\tfinal_url";

    public static string Write(IEnumerable<UrlContractEntry> entries)
    {
        var builder = new StringBuilder(Header).Append('\n');
        foreach (var e in entries.OrderBy(e => e.Url, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"{Escape(e.Url)}\t{e.Class}\t{e.Status}\t{Escape(e.Location ?? string.Empty)}\t{e.FinalStatus}\t{Escape(e.FinalUrl)}\n");
        }

        return builder.ToString();
    }

    public static IReadOnlyList<UrlContractEntry> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(line =>
            {
                var f = line.Split('\t');
                if (f.Length != 6)
                {
                    throw new FormatException($"Expected 6 tab-separated fields: '{line}'.");
                }

                return new UrlContractEntry(
                    f[0],
                    Enum.Parse<LegacyUrlClass>(f[1]),
                    int.Parse(f[2], CultureInfo.InvariantCulture),
                    f[3].Length == 0 ? null : f[3],
                    int.Parse(f[4], CultureInfo.InvariantCulture),
                    f[5]);
            })
            .ToList();
    }

    // URLs captured from the wild can contain tabs or newlines only in malformed form; keep the file parseable.
    private static string Escape(string value) => value.Replace("\t", "%09", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal);
}
