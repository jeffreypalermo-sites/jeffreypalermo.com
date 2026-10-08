namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>tests/contract/dns-inventory.tsv</c>: what the name servers of <c>jeffreypalermo.com</c> answered on 2026-10-08,
/// and what the Azure DNS zone does with each record (ADR-0016).
/// </summary>
internal sealed record DnsInventoryRecord(string Name, string Type, int Ttl, string Value, string Zone, string Note)
{
    public const string ZoneName = "jeffreypalermo.com";

    /// <summary>The value as a zone of Azure DNS holds it: a name without the dot at its end, a text without its quotes.</summary>
    public string ValueInTheZone => Type is "TXT" or "A" ? Value : Value.TrimEnd('.');

    public static List<DnsInventoryRecord> Read() =>
        [.. File.ReadAllLines(Path.Join(TestPaths.Contract, "dns-inventory.tsv"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Skip(1)
            .Select(line => line.Split('\t'))
            .Select(fields => fields.Length == 6
                ? new DnsInventoryRecord(fields[0], fields[1], int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture), fields[3], fields[4], fields[5])
                : throw new FormatException($"Expected 6 tab-separated fields (name, type, ttl, value, zone, note): '{string.Join('\t', fields)}'."))];
}
