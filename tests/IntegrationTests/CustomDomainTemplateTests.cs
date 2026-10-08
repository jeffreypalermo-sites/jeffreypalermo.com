using System.Text.Json;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// The custom domain in <c>deploy/infra/main.bicep</c> (ADR-0014), on the template as the Bicep compiler writes it.
/// The domain is prepared and switched off: no environment lists a host name. So the first thing to prove is that
/// with no host name the template deploys what it deployed without this part, resource for resource.
/// </summary>
public sealed class CustomDomainTemplateTests
{
    /// <summary>Everything the template deployed before it knew of host names: one of each, and one app and one origin per region.</summary>
    private static readonly string[] BeforeHostNames =
        ["profiles *", "containerApps *", "afdEndpoints *", "originGroups web", "origins *", "routes web"];

    private static readonly string[] Apex = ["jeffreypalermo.com"];

    private static readonly string[] WwwAndFeeds = ["www.jeffreypalermo.com", "feeds.jeffreypalermo.com"];

    private static List<object?> Regions(int count) => [.. Enumerable.Range(0, count).Select(_ => (object?)"a region")];

    private static List<object?> Names(params string[] names) => [.. names];

    private static bool KnowsOfHostNames(JsonElement part) => part.GetRawText().Contains("HostNames", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task TheTemplateCompilesWithoutAWarning()
    {
        var template = await CompiledTemplate.CompileAsync();

        Assert.DoesNotMatch(" : (Warning|Error) ", template.Diagnostics);
    }

    private static JsonElement TheModule(CompiledTemplate template) =>
        template.Resources.Single(resource => resource.GetProperty("type").GetString() == "Microsoft.Resources/deployments");

    [Theory]
    [InlineData(true, 2)]
    [InlineData(true, 11)]
    [InlineData(false, 1)]
    public async Task WithNoHostNameTheTemplateDeploysWhatItDidBeforeItKnewOfHostNames(bool frontDoor, int regions)
    {
        var template = (await CompiledTemplate.CompileAsync()).With(("frontDoor", frontDoor), ("regions", Regions(regions)), ("hostNames", Names()), ("redirectHostNames", Names()));
        var deployed = template.Resources.Where(resource => template.Instances(resource) > 0).ToList();

        // Exactly the resources of before, as many of each as before.
        Assert.Equal(
            frontDoor ? BeforeHostNames : ["containerApps *"],
            deployed.Select(CompiledTemplate.Kind));
        Assert.Equal(
            frontDoor ? [1, regions, 1, 1, regions, 1] : [regions],
            deployed.Select(template.Instances));
        // And none of them is written in terms of the host names: no list of names can change what they are.
        Assert.All(template.Resources.Where(resource => BeforeHostNames.Contains(CompiledTemplate.Kind(resource))), resource =>
            Assert.False(KnowsOfHostNames(resource), $"{CompiledTemplate.Kind(resource)} depends on the host names."));
        // The one resource that is written in terms of them, the module with everything a host name needs, is not deployed.
        var added = Assert.Single(template.Resources, KnowsOfHostNames);
        Assert.Equal(TheModule(template).GetRawText(), added.GetRawText());
        Assert.Equal(0, template.Instances(added));
        Assert.Equal(BeforeHostNames.Length + 1, template.Resources.Count());
    }

    /// <summary>
    /// The switched-off state uses nothing of Azure Resource Manager that the template did not use before: one more
    /// resource whose condition is false, as the Front Door's are in tdd, and one more output behind an "if".
    /// </summary>
    [Fact]
    public async Task TheModuleIsBehindOneConditionAndSoIsItsOutput()
    {
        var template = await CompiledTemplate.CompileAsync();

        Assert.Equal("[variables('customDomain')]", TheModule(template).GetProperty("condition").GetString());
        Assert.False(TheModule(template).TryGetProperty("copy", out _));
        Assert.Equal(
            "[and(parameters('frontDoor'), not(empty(concat(parameters('hostNames'), parameters('redirectHostNames')))))]",
            template.Root.GetProperty("variables").GetProperty("customDomain").GetString());
        Assert.StartsWith("[if(variables('customDomain'), reference(", template.Root.GetProperty("outputs").GetProperty("hostNames").GetProperty("value").GetString(), StringComparison.Ordinal);
        Assert.EndsWith(".outputs.hostNames.value, createArray())]", template.Root.GetProperty("outputs").GetProperty("hostNames").GetProperty("value").GetString(), StringComparison.Ordinal);
        // Environments of one tier share a resource group: each has a deployment of its own name.
        Assert.Equal("[format('custom-domains-{0}', parameters('environmentName'))]", TheModule(template).GetProperty("name").GetString());
    }

    [Fact]
    public async Task TheListsAreEmptyWhenNobodyGivesThem()
    {
        var template = (await CompiledTemplate.CompileAsync()).With(("frontDoor", true), ("regions", Regions(11)));

        Assert.Equal(0, template.Instances(TheModule(template)));
        Assert.False((bool)template.Evaluate(template.Root.GetProperty("variables").GetProperty("customDomain"))!);
    }

    [Fact]
    public async Task WithNoHostNameTheOutputsAreTheOnesOfBeforeAndAnEmptyList()
    {
        var template = (await CompiledTemplate.CompileAsync()).With(("frontDoor", true), ("regions", Regions(2)), ("hostNames", Names()), ("redirectHostNames", Names()));
        var outputs = template.Root.GetProperty("outputs").EnumerateObject().ToList();

        Assert.Equal(["regions", "frontDoorUrl", "frontDoorEndpointId", "hostNames"], outputs.Select(output => output.Name));
        Assert.All(outputs.Where(output => output.Name != "hostNames"), output => Assert.False(KnowsOfHostNames(output.Value), $"The output {output.Name} depends on the host names."));
        // if(false, <the module's output>, createArray()): the empty list.
        Assert.False((bool)template.Evaluate(template.Root.GetProperty("variables").GetProperty("customDomain"))!);
    }

    /// <summary>The day of the DNS move: the canonical host, and the two names the site redirects to it.</summary>
    [Fact]
    public async Task TheHostNamesOfTheDayAddADomainEachAndARouteForEachWayTheSiteAnswers()
    {
        var template = (await CompiledTemplate.CompileAsync()).With(("frontDoor", true), ("regions", Regions(11)), ("hostNames", Names(Apex)), ("redirectHostNames", Names(WwwAndFeeds)));
        var module = template.Module(TheModule(template));
        var instances = module.Resources.ToDictionary(CompiledTemplate.Kind, module.Instances);

        Assert.Equal(1, template.Instances(TheModule(template)));
        Assert.Equal(3, instances.Count);
        Assert.Equal(3, instances["customDomains *"]);
        Assert.Equal(1, instances["routes web-hosts"]);
        Assert.Equal(1, instances["routes web-redirects"]);
        Assert.Equal(3, module.OutputItems("hostNames"));
        // What was there before is there as before.
        Assert.Equal([1, 11, 1, 1, 11, 1], template.Resources.Where(resource => BeforeHostNames.Contains(CompiledTemplate.Kind(resource))).Select(template.Instances));
    }

    [Theory]
    [InlineData(new[] { "uat.jeffreypalermo.com" }, new string[0], 1, 1, 0)]
    [InlineData(new[] { "jeffreypalermo.com" }, new[] { "www.jeffreypalermo.com" }, 2, 1, 1)]
    public async Task ARouteExistsOnlyForAKindOfHostNameThatIsListed(string[] pages, string[] redirects, int domains, int pageRoutes, int redirectRoutes)
    {
        var template = (await CompiledTemplate.CompileAsync()).With(("frontDoor", true), ("regions", Regions(2)), ("hostNames", Names(pages)), ("redirectHostNames", Names(redirects)));
        var module = template.Module(TheModule(template));
        var instances = module.Resources.ToDictionary(CompiledTemplate.Kind, module.Instances);

        Assert.Equal(1, template.Instances(TheModule(template)));
        Assert.Equal(domains, instances["customDomains *"]);
        Assert.Equal(pageRoutes, instances["routes web-hosts"]);
        Assert.Equal(redirectRoutes, instances["routes web-redirects"]);
    }

    /// <summary>A custom domain needs the Front Door. deploy.ps1 refuses such settings; the template deploys none either way.</summary>
    [Fact]
    public async Task WithoutAFrontDoorHostNamesDeployNothing()
    {
        var template = (await CompiledTemplate.CompileAsync()).With(("frontDoor", false), ("regions", Regions(1)), ("hostNames", Names(Apex)), ("redirectHostNames", Names(WwwAndFeeds)));

        Assert.Equal(0, template.Instances(TheModule(template)));
        Assert.Equal(["containerApps *"], template.Resources.Where(resource => template.Instances(resource) > 0).Select(CompiledTemplate.Kind));
    }

    /// <summary>
    /// The names with pages are kept at the edge exactly as the endpoint's own name is. The names that only redirect
    /// have a route with no cache: the edge keeps nothing for them and gives them nothing it keeps for another name.
    /// </summary>
    [Fact]
    public async Task TheRouteOfThePageHostsCachesLikeTheEndpointsOwnAndTheRouteOfTheRedirectHostsNotAtAll()
    {
        var template = await CompiledTemplate.CompileAsync();
        var module = template.Module(TheModule(template));
        var own = template.Resources.Single(resource => CompiledTemplate.Kind(resource) == "routes web").GetProperty("properties");
        var routes = module.Resources.Where(resource => CompiledTemplate.Kind(resource).StartsWith("routes ", StringComparison.Ordinal))
            .ToDictionary(CompiledTemplate.Kind, resource => resource.GetProperty("properties"));

        Assert.Equal(2, routes.Count);
        // The module is given the very settings the endpoint's own route has.
        Assert.Equal("[variables('routeCache')]", own.GetProperty("cacheConfiguration").GetString());
        Assert.Equal("[variables('routeCache')]", TheModule(template).GetProperty("properties").GetProperty("parameters").GetProperty("routeCache").GetProperty("value").GetString());
        Assert.Equal("[parameters('routeCache')]", routes["routes web-hosts"].GetProperty("cacheConfiguration").GetString());
        Assert.False(routes["routes web-redirects"].TryGetProperty("cacheConfiguration", out _));

        // The endpoint's own name stays with the route it had; the new routes answer for their host names only.
        Assert.Equal("Enabled", own.GetProperty("linkToDefaultDomain").GetString());
        Assert.False(own.TryGetProperty("customDomains", out _));
        Assert.False(own.TryGetProperty("copy", out _));
        Assert.All(routes.Values, route =>
        {
            Assert.Equal("Disabled", route.GetProperty("linkToDefaultDomain").GetString());
            // Everything else as the endpoint's own route: every pattern, HTTPS only.
            Assert.All((string[])["supportedProtocols", "patternsToMatch", "forwardingProtocol", "httpsRedirect", "enabledState"], property =>
                Assert.Equal(JsonSerializer.Serialize(own.GetProperty(property)), JsonSerializer.Serialize(route.GetProperty(property))));
            // And the same regions: the origin group "web" of the same profile.
            Assert.Equal("[resourceId('Microsoft.Cdn/profiles/originGroups', parameters('profileName'), parameters('originGroupName'))]", route.GetProperty("originGroup").GetProperty("id").GetString());
        });
        Assert.Equal("web", TheModule(template).GetProperty("properties").GetProperty("parameters").GetProperty("originGroupName").GetProperty("value").GetString());

        // Each route is given the domains of its own list: the names with pages first in the list of all, the others after.
        var copies = (JsonElement route) => route.GetProperty("copy").EnumerateArray().Single();
        Assert.Equal("[length(parameters('hostNames'))]", copies(routes["routes web-hosts"]).GetProperty("count").GetString());
        Assert.Contains("variables('allHostNames')[copyIndex('customDomains')]", copies(routes["routes web-hosts"]).GetProperty("input").GetRawText(), StringComparison.Ordinal);
        Assert.Equal("[length(parameters('redirectHostNames'))]", copies(routes["routes web-redirects"]).GetProperty("count").GetString());
        Assert.Contains("variables('allHostNames')[add(length(parameters('hostNames')), copyIndex('customDomains'))]", copies(routes["routes web-redirects"]).GetProperty("input").GetRawText(), StringComparison.Ordinal);
        Assert.Equal("[concat(parameters('hostNames'), parameters('redirectHostNames'))]", module.Root.GetProperty("variables").GetProperty("allHostNames").GetString());
    }

    [Fact]
    public async Task EveryHostNameGetsACertificateTheFrontDoorManages()
    {
        var template = await CompiledTemplate.CompileAsync();
        var module = template.Module(TheModule(template));
        var domain = module.Resources.Single(resource => CompiledTemplate.Kind(resource) == "customDomains *").GetProperty("properties");

        Assert.Equal("[variables('allHostNames')[copyIndex()]]", domain.GetProperty("hostName").GetString());
        Assert.Equal("ManagedCertificate", domain.GetProperty("tlsSettings").GetProperty("certificateType").GetString());
        Assert.Equal("TLS12", domain.GetProperty("tlsSettings").GetProperty("minimumTlsVersion").GetString());
    }

    /// <summary>What deploy.ps1 prints for DNS comes from here: the record that proves the name, and where the name points.</summary>
    [Fact]
    public async Task TheOutputsGiveForEveryHostNameWhatDnsNeeds()
    {
        var template = await CompiledTemplate.CompileAsync();
        var item = template.Module(TheModule(template)).Root.GetProperty("outputs").GetProperty("hostNames").GetProperty("copy").GetProperty("input");

        Assert.Equal(
            ["hostName", "kept", "validationState", "validationRecord", "validationToken", "validationExpires", "target"],
            item.EnumerateObject().Select(property => property.Name));
        Assert.Equal("[format('_dnsauth.{0}', variables('allHostNames')[copyIndex()])]", item.GetProperty("validationRecord").GetString());
        Assert.Equal("[less(copyIndex(), length(parameters('hostNames')))]", item.GetProperty("kept").GetString());
        Assert.Contains(".domainValidationState]", item.GetProperty("validationState").GetString(), StringComparison.Ordinal);
        Assert.Contains("'validationProperties'), 'validationToken'), '')]", item.GetProperty("validationToken").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("'2024-02-01').hostName]", item.GetProperty("target").GetString(), StringComparison.Ordinal);
        Assert.Contains("Microsoft.Cdn/profiles/afdEndpoints", item.GetProperty("target").GetString(), StringComparison.Ordinal);
    }
}
