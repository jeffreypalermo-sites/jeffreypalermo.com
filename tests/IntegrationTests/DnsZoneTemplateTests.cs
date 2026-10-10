using System.Globalization;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The DNS zone as code (ADR-0016): <c>deploy/infra/dns-zone.bicep</c> compiled for real, and every record set it
/// deploys worked out and held against <c>tests/contract/dns-inventory.tsv</c>, the reading of the domain's public
/// DNS. A record that must survive cannot be dropped, or changed, without that file saying so.
/// </summary>
public sealed class DnsZoneTemplateTests
{
    private const string Zone = DnsInventoryRecord.ZoneName;
    private const string EndpointId = "/subscriptions/1/resourceGroups/rg-jpcom-prod/providers/Microsoft.Cdn/profiles/afd-jpcom-prod/afdEndpoints/jpcom-prod";
    private const string EndpointHost = "jpcom-prod-d8e7htexeqewe0hr.z02.azurefd.net";

    private static List<object?> Names(params string[] names) => [.. names];

    private static async Task<CompiledTemplate> ZoneAsync(string[]? hostLabels = null, string[]? validationLabels = null, string[]? validationTokens = null) =>
        (await CompiledTemplate.CompileAsync("dns-zone.bicep")).With(
            ("zoneName", Zone), ("system", "jpcom"), ("environmentName", "prod"),
            ("hostLabels", Names(hostLabels ?? [])), ("frontDoorEndpointId", hostLabels is null ? string.Empty : EndpointId), ("frontDoorHostName", hostLabels is null ? string.Empty : EndpointHost),
            ("validationLabels", Names(validationLabels ?? [])), ("validationTokens", Names(validationTokens ?? [])));

    /// <summary>A record set as one line per value: <c>@ MX 3600 0 smtp.secureserver.net</c>.</summary>
    private static List<string> Records(CompiledTemplate template) =>
        [.. template.Deploy()
            .Where(resource => resource.Type != "Microsoft.Network/dnsZones")
            .SelectMany(resource =>
            {
                var type = resource.Type[(resource.Type.LastIndexOf('/') + 1)..];
                var name = resource.Name[(resource.Name.IndexOf('/', StringComparison.Ordinal) + 1)..];
                Assert.StartsWith($"{Zone}/", resource.Name, StringComparison.Ordinal);
                return Values(type, resource.Properties).Select(value => $"{name} {type} {resource.Properties["TTL"]} {value}");
            })
            .Order(StringComparer.Ordinal)];

    private static IEnumerable<string> Values(string type, Dictionary<string, object?> properties)
    {
        static Dictionary<string, object?> Object(object? value) => (Dictionary<string, object?>)value!;
        static List<object?> List(object? value) => (List<object?>)value!;

        if (properties.TryGetValue("targetResource", out var target))
        {
            return [$"alias of {Object(target)["id"]}"];
        }

        return type switch
        {
            "A" => List(properties["ARecords"]).Select(record => (string)Object(record)["ipv4Address"]!),
            "MX" => List(properties["MXRecords"]).Select(record => $"{Object(record)["preference"]} {Object(record)["exchange"]}"),
            "TXT" => List(properties["TXTRecords"]).Select(record => string.Concat(List(Object(record)["value"]).Cast<string>())),
            "CNAME" => [(string)Object(properties["CNAMERecord"])["cname"]!],
            _ => throw new NotSupportedException($"The zone holds a record of a type this test does not read: {type}"),
        };
    }

    private static string Line(DnsInventoryRecord record, int ttl) =>
        $"{record.Name} {record.Type} {ttl.ToString(CultureInfo.InvariantCulture)} {record.ValueInTheZone}";

    [Fact]
    public async Task TheTemplateCompilesWithoutAWarning()
    {
        var template = await CompiledTemplate.CompileAsync("dns-zone.bicep");

        Assert.DoesNotMatch(" : (Warning|Error) ", template.Diagnostics);
    }

    /// <summary>
    /// Before any name is the site's: the zone answers as the domain's DNS answered on 2026-10-08, record for record.
    /// Were it delegated today, mail and every reader would go where they go now.
    /// </summary>
    [Fact]
    public async Task WithNoHostNameTheZoneHoldsExactlyTheRecordsOfTheInventoryThatSurvive()
    {
        var inventory = DnsInventoryRecord.Read();
        var expected = inventory
            .Where(record => record.Zone is "kept" or "site")
            // What the move changes has five minutes, whatever it had; mail keeps the hour it had.
            .Select(record => Line(record, record.Zone == "site" ? 300 : record.Ttl))
            .Order(StringComparer.Ordinal)
            .ToList();

        var records = Records(await ZoneAsync());

        Assert.Equal(expected, records);
        Assert.Equal(10, records.Count);
    }

    [Fact]
    public async Task MailAndWhatProvesMailAreInTheZoneExactlyAsTheyWereRead()
    {
        var kept = DnsInventoryRecord.Read().Where(record => record.Zone == "kept").ToList();

        var records = Records(await ZoneAsync());

        Assert.Equal(
            ["@ MX", "@ MX", "@ TXT", "_dmarc TXT", "wpcloud1._domainkey CNAME", "wpcloud2._domainkey CNAME"],
            kept.Select(record => $"{record.Name} {record.Type}"));
        Assert.All(kept, record => Assert.Contains(Line(record, record.Ttl), records));
        Assert.Contains("@ MX 3600 0 smtp.secureserver.net", records);
        Assert.Contains("@ MX 3600 10 mailstore1.secureserver.net", records);
        Assert.Contains("@ TXT 3600 v=spf1 include:_spf.wpcloud.com ~all", records);
        Assert.Contains("_dmarc TXT 3600 v=DMARC1;p=none;", records);
        // Mail has an hour, as it had: nothing about it changes with the move.
        Assert.All(kept, record => Assert.Equal(3600, record.Ttl));
    }

    /// <summary>The day's names are the site's: mail is untouched by it, to the letter.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("@ www feeds")]
    [InlineData("@ www feeds blog")]
    public async Task WhateverTheHostNamesMailStaysExactlyAsItWasRead(string names)
    {
        var hostLabels = names.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = DnsInventoryRecord.Read().Where(record => record.Zone == "kept").Select(record => Line(record, record.Ttl)).ToList();

        var records = Records(await ZoneAsync(hostLabels, hostLabels, [.. hostLabels.Select(label => $"token-of-{label}")]));

        Assert.All(kept, record => Assert.Contains(record, records));
        Assert.Equal(kept.Count, records.Count(record => record.Contains(" 3600 ", StringComparison.Ordinal)));
    }

    /// <summary>What was decided not to carry over is not in the zone, with or without host names (ADR-0016).</summary>
    [Theory]
    [InlineData("")]
    [InlineData("@ www feeds")]
    public async Task TheWildcardAndWordPressComsOwnRecordAreNotInTheZone(string names)
    {
        var hostLabels = names.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var dropped = DnsInventoryRecord.Read().Where(record => record.Zone == "dropped").ToList();

        var records = Records(await ZoneAsync(hostLabels, hostLabels, [.. hostLabels.Select(label => $"token-of-{label}")]));

        Assert.Equal(["_domainconnect TXT", "* CNAME"], dropped.Select(record => $"{record.Name} {record.Type}"));
        Assert.All(dropped, record => Assert.DoesNotContain(records, line => line.StartsWith($"{record.Name} ", StringComparison.Ordinal)));
        Assert.All(dropped, record => Assert.True(record.Note.Length > 20, $"{record.Name} is dropped without a reason."));
        // The zone's own NS and SOA records are Azure DNS's to write.
        Assert.DoesNotContain(records, line => line.Contains(" NS ", StringComparison.Ordinal) || line.Contains(" SOA ", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryRecordOfTheInventoryIsAccountedFor()
    {
        var inventory = DnsInventoryRecord.Read();

        Assert.Equal(16, inventory.Count);
        Assert.All(inventory, record => Assert.Contains(record.Zone, (string[])["kept", "site", "own", "dropped"]));
        Assert.All(inventory, record => Assert.False(string.IsNullOrWhiteSpace(record.Note), $"{record.Name} {record.Type} has no note."));
        Assert.Equal(["A", "A", "CNAME", "CNAME"], inventory.Where(record => record.Zone == "site").Select(record => record.Type));
        Assert.Equal(["NS", "NS", "NS", "SOA"], inventory.Where(record => record.Zone == "own").Select(record => record.Type));
    }

    /// <summary>
    /// The day's three names: the zone's own name is an alias of the Front Door endpoint (it cannot be a CNAME),
    /// www and feeds are CNAMEs of it, and each has the TXT record that proves it, made of the Front Door's token.
    /// </summary>
    [Fact]
    public async Task TheHostNamesOfTheDayAreAnsweredByTheFrontDoor()
    {
        var records = Records(await ZoneAsync(["@", "www", "feeds"], ["@", "www", "feeds"], ["token-apex", "token-www", "token-feeds"]));

        Assert.Contains($"@ A 300 alias of {EndpointId}", records);
        Assert.Contains($"www CNAME 300 {EndpointHost}", records);
        Assert.Contains($"feeds CNAME 300 {EndpointHost}", records);
        Assert.Contains("_dnsauth TXT 300 token-apex", records);
        Assert.Contains("_dnsauth.www TXT 300 token-www", records);
        Assert.Contains("_dnsauth.feeds TXT 300 token-feeds", records);
        // Nothing of what the three names were is left beside it: one record set per name and type.
        Assert.DoesNotContain(records, line => line.Contains("192.0.78.", StringComparison.Ordinal));
        Assert.DoesNotContain(records, line => line.Contains("feedproxy", StringComparison.Ordinal));
        Assert.Single(records, line => line.StartsWith("www CNAME ", StringComparison.Ordinal));
        Assert.Single(records, line => line.StartsWith("feeds CNAME ", StringComparison.Ordinal));
        Assert.Single(records, line => line.StartsWith("@ A ", StringComparison.Ordinal));
        Assert.Equal(12, records.Count);
    }

    /// <summary>A name is the site's own only once it is a host name: the others stay as they were read.</summary>
    [Fact]
    public async Task ANameThatIsNotAHostNameYetIsAnsweredAsItWas()
    {
        var records = Records(await ZoneAsync(["@"], ["@"], ["token-apex"]));

        Assert.Contains($"@ A 300 alias of {EndpointId}", records);
        Assert.Contains($"www CNAME 300 {Zone}", records);
        Assert.Contains("feeds CNAME 300 1i4ygfi.feedproxy.ghs.google.com", records);
        Assert.Contains("_dnsauth TXT 300 token-apex", records);
        Assert.DoesNotContain(records, line => line.StartsWith("_dnsauth.", StringComparison.Ordinal));
    }

    /// <summary>
    /// A new custom domain has no token yet, or the Front Door gives none any more: its address record is there, and
    /// its TXT record comes with the deployment that knows the token.
    /// </summary>
    [Fact]
    public async Task AHostNameWithoutATokenHasItsAddressRecordAndNoTxtRecord()
    {
        var records = Records(await ZoneAsync(["@", "www", "feeds"], ["www"], ["token-www"]));

        Assert.Contains($"@ A 300 alias of {EndpointId}", records);
        Assert.Contains($"feeds CNAME 300 {EndpointHost}", records);
        Assert.Equal(["_dnsauth.www TXT 300 token-www"], records.Where(line => line.StartsWith("_dnsauth", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AnyOtherHostNameInTheZoneIsACnameOfTheEndpoint()
    {
        var records = Records(await ZoneAsync(["blog"], ["blog"], ["token-blog"]));

        Assert.Contains($"blog CNAME 300 {EndpointHost}", records);
        Assert.Contains("_dnsauth.blog TXT 300 token-blog", records);
        // The zone's own name is not a host name here: it stays WordPress.com's.
        Assert.Contains("@ A 300 192.0.78.168", records);
        Assert.Contains("@ A 300 192.0.78.213", records);
    }

    /// <summary>Everything that changes with the move, or with a renewed certificate, is everywhere within five minutes.</summary>
    [Fact]
    public async Task WhatTheMoveChangesHasFiveMinutesAndMailHasAnHour()
    {
        var records = Records(await ZoneAsync(["@", "www", "feeds"], ["@", "www", "feeds"], ["a", "b", "c"]));

        Assert.All(records, line =>
        {
            var mail = line.Contains(" MX ", StringComparison.Ordinal) || line.StartsWith("@ TXT ", StringComparison.Ordinal) || line.StartsWith("_dmarc ", StringComparison.Ordinal) || line.Contains("_domainkey ", StringComparison.Ordinal);
            Assert.Contains(mail ? " 3600 " : " 300 ", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task TheZoneIsAPublicZoneNamedAsTheSettingsSayAndGivesItsNameServers()
    {
        var template = await ZoneAsync();
        var zone = Assert.Single(template.Deploy(), resource => resource.Type == "Microsoft.Network/dnsZones");

        Assert.Equal(Zone, zone.Name);
        Assert.Equal("Public", zone.Properties["zoneType"]);
        Assert.Equal(["nameServers", "zoneId"], template.Root.GetProperty("outputs").EnumerateObject().Select(output => output.Name));
        Assert.EndsWith(".nameServers]", template.Root.GetProperty("outputs").GetProperty("nameServers").GetProperty("value").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The zone is not in the site's stack, which deletes what leaves its template: the site's template knows nothing
    /// of DNS, and so is, compiled, what it was before there was a zone.
    /// </summary>
    [Fact]
    public async Task TheSitesTemplateHoldsNoDnsAtAll()
    {
        var site = await CompiledTemplate.CompileAsync();

        Assert.DoesNotContain("Microsoft.Network", site.Root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dnsZone", site.Root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            // The last three are the access log's (ADR-0023): nothing of DNS was added with them.
            ["system", "environmentName", "version", "registryServer", "pullIdentityId", "regions", "frontDoor", "port", "hostNames", "redirectHostNames", "edgeLogs", "edgeLogsRetentionDays", "edgeLogsDailyCapGb"],
            site.Root.GetProperty("parameters").EnumerateObject().Select(parameter => parameter.Name));
    }

    /// <summary>And the zone's template holds nothing but the zone: no part of the site can leave with it.</summary>
    [Fact]
    public async Task TheZonesTemplateHoldsNothingButTheZoneAndItsRecords()
    {
        var template = await CompiledTemplate.CompileAsync("dns-zone.bicep");

        Assert.All(template.Resources, resource => Assert.StartsWith("Microsoft.Network/dnsZones", resource.GetProperty("type").GetString(), StringComparison.Ordinal));
        Assert.DoesNotContain("Microsoft.Authorization", template.Root.GetRawText(), StringComparison.Ordinal);
    }
}
