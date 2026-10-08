using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// <c>deploy/infra/main.bicep</c> compiled for real with <c>az bicep build</c> (a local compile: it reaches no Azure),
/// and enough of Azure Resource Manager's template language to say which resources a set of parameters deploys:
/// each resource's <c>condition</c> and the <c>count</c> of its copy loop. A function this does not know fails the
/// test that meets it, so a condition can never be passed over unread.
/// </summary>
internal sealed class CompiledTemplate
{
    private readonly JsonElement _template;
    private readonly Dictionary<string, object?> _parameters;

    /// <summary>For a module: the template that deploys it, and the values it gives the module's parameters.</summary>
    private readonly CompiledTemplate? _outer;
    private readonly JsonElement _given;

    private CompiledTemplate(JsonElement template, string diagnostics, Dictionary<string, object?> parameters, CompiledTemplate? outer = null, JsonElement given = default)
    {
        _template = template;
        Diagnostics = diagnostics;
        _parameters = parameters;
        _outer = outer;
        _given = given;
    }

    /// <summary>What the compiler wrote beside the template: its warnings and errors, and the CLI's notices.</summary>
    public string Diagnostics { get; }

    public JsonElement Root => _template;

    private static readonly Lazy<Task<CompiledTemplate>> Compiled = new(CompileOnceAsync);

    /// <summary>The template, compiled once for all the tests that read it.</summary>
    public static Task<CompiledTemplate> CompileAsync() => Compiled.Value;

    private static async Task<CompiledTemplate> CompileOnceAsync()
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["bicep", "build", "--file", Path.Join(TestPaths.RepositoryRoot, "deploy", "infra", "main.bicep"), "--stdout"])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start az.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"az bicep build failed ({process.ExitCode}):\n{await error}");
        return new CompiledTemplate(JsonDocument.Parse(await output).RootElement, await error, []);
    }

    /// <summary>The same template with these parameter values; a parameter not given has the template's default.</summary>
    public CompiledTemplate With(params (string Name, object? Value)[] parameters)
    {
        var values = new Dictionary<string, object?>(_parameters, StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
        {
            values[name] = value;
        }

        return new CompiledTemplate(_template, Diagnostics, values);
    }

    public IEnumerable<JsonElement> Resources => Root.GetProperty("resources").EnumerateArray();

    /// <summary>
    /// The template of a module (a nested deployment), with the parameters this template gives it. A value the
    /// module is given is worked out here, in the template that gives it, when the module asks for it.
    /// </summary>
    public CompiledTemplate Module(JsonElement deployment) =>
        new(deployment.GetProperty("properties").GetProperty("template"), Diagnostics, [], this, deployment.GetProperty("properties").GetProperty("parameters"));

    /// <summary>How many of a resource the parameters deploy: none when its condition is false, else its copy count, else one.</summary>
    public int Instances(JsonElement resource)
    {
        if (resource.TryGetProperty("condition", out var condition) && !(bool)Evaluate(condition)!)
        {
            return 0;
        }

        return resource.TryGetProperty("copy", out var copy) ? checked((int)(long)Evaluate(copy.GetProperty("count"))!) : 1;
    }

    /// <summary>How many items an output that is a loop gives.</summary>
    public int OutputItems(string name) =>
        checked((int)(long)Evaluate(Root.GetProperty("outputs").GetProperty(name).GetProperty("copy").GetProperty("count"))!);

    /// <summary>A resource's type and the last part of its name, when that part is a fixed text: <c>routes web</c>.</summary>
    public static string Kind(JsonElement resource)
    {
        var type = resource.GetProperty("type").GetString()!;
        var last = System.Text.RegularExpressions.Regex.Match(resource.GetProperty("name").GetString()!, @"'(?<last>[^']*)'\)\]$");
        return $"{type[(type.LastIndexOf('/') + 1)..]} {(last.Success ? last.Groups["last"].Value : "*")}";
    }

    public object? Evaluate(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return value.GetInt64();
            case JsonValueKind.Array:
                return value.EnumerateArray().Select(Evaluate).ToList();
            case JsonValueKind.String:
                var text = value.GetString()!;
                if (!text.StartsWith('[') || text.StartsWith("[[", StringComparison.Ordinal))
                {
                    return text;
                }

                var position = 1;
                var result = Expression(text, ref position);
                Assert.True(position == text.Length - 1, $"Could not read all of {text}");
                return result;
            default:
                throw new NotSupportedException($"A {value.ValueKind} is not evaluated: {value}");
        }
    }

    private object? Expression(string text, ref int position)
    {
        SkipSpaces(text, ref position);
        if (text[position] == '\'')
        {
            var end = text.IndexOf('\'', position + 1);
            var literal = text[(position + 1)..end];
            position = end + 1;
            return literal;
        }

        if (char.IsAsciiDigit(text[position]))
        {
            var from = position;
            while (char.IsAsciiDigit(text[position]))
            {
                position++;
            }

            return long.Parse(text[from..position], CultureInfo.InvariantCulture);
        }

        var nameFrom = position;
        while (char.IsAsciiLetter(text[position]))
        {
            position++;
        }

        var function = text[nameFrom..position];
        Assert.True(text[position] == '(', $"Expected a function call at {position} in {text}");
        position++;
        var arguments = new List<object?>();
        SkipSpaces(text, ref position);
        while (text[position] != ')')
        {
            arguments.Add(Expression(text, ref position));
            SkipSpaces(text, ref position);
            if (text[position] == ',')
            {
                position++;
            }
        }

        position++;
        return function switch
        {
            "parameters" => Parameter((string)arguments[0]!),
            "variables" => Evaluate(Root.GetProperty("variables").GetProperty((string)arguments[0]!)),
            "if" => (bool)arguments[0]! ? arguments[1] : arguments[2],
            "and" => arguments.All(argument => (bool)argument!),
            "or" => arguments.Any(argument => (bool)argument!),
            "not" => !(bool)arguments[0]!,
            "empty" => arguments[0] is null || (arguments[0] is string s ? s.Length == 0 : ((List<object?>)arguments[0]!).Count == 0),
            "length" => arguments[0] is string t ? t.Length : (long)((List<object?>)arguments[0]!).Count,
            "concat" => arguments.SelectMany(argument => (List<object?>)argument!).ToList(),
            "createArray" => arguments,
            _ => throw new NotSupportedException($"The function '{function}' is not known to this test: {text}"),
        };
    }

    private object? Parameter(string name)
    {
        if (_parameters.TryGetValue(name, out var given))
        {
            return given;
        }

        if (_outer is not null && _given.TryGetProperty(name, out var fromOuter))
        {
            return _outer.Evaluate(fromOuter.GetProperty("value"));
        }

        var declared = Root.GetProperty("parameters").GetProperty(name);
        return declared.TryGetProperty("defaultValue", out var value)
            ? Evaluate(value)
            : throw new InvalidOperationException($"The test gives no value for the parameter '{name}', and the template has no default.");
    }

    private static void SkipSpaces(string text, ref int position)
    {
        while (text[position] == ' ')
        {
            position++;
        }
    }
}
