using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace JeffreyPalermo.IntegrationTests;

/// <summary>
/// A Bicep file of <c>deploy/infra</c> compiled for real with <c>az bicep build</c> (a local compile: it reaches no
/// Azure), and enough of Azure Resource Manager's template language to say what a set of parameters deploys: each
/// resource's <c>condition</c>, the <c>count</c> of its copy loop, and, for <see cref="Deploy"/>, its name and its
/// properties worked out. A function this does not know fails the test that meets it, so nothing is passed over
/// unread.
/// </summary>
internal sealed class CompiledTemplate
{
    private readonly JsonElement _template;
    private readonly Dictionary<string, object?> _parameters;

    /// <summary>For a module: the template that deploys it, and the values it gives the module's parameters.</summary>
    private readonly CompiledTemplate? _outer;
    private readonly JsonElement _given;

    /// <summary>Which turn of a resource's copy loop is being worked out; null outside a loop.</summary>
    private readonly long? _copyIndex;

    private CompiledTemplate(JsonElement template, string diagnostics, Dictionary<string, object?> parameters, CompiledTemplate? outer = null, JsonElement given = default, long? copyIndex = null)
    {
        _template = template;
        Diagnostics = diagnostics;
        _parameters = parameters;
        _outer = outer;
        _given = given;
        _copyIndex = copyIndex;
    }

    /// <summary>What the compiler wrote beside the template: its warnings and errors, and the CLI's notices.</summary>
    public string Diagnostics { get; }

    public JsonElement Root => _template;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<CompiledTemplate>>> Compiled = new(StringComparer.Ordinal);

    /// <summary>A template of <c>deploy/infra</c>, compiled once for all the tests that read it. The site's by default.</summary>
    public static Task<CompiledTemplate> CompileAsync(string file = "main.bicep") =>
        Compiled.GetOrAdd(file, name => new Lazy<Task<CompiledTemplate>>(() => CompileOnceAsync(name))).Value;

    private static async Task<CompiledTemplate> CompileOnceAsync(string file)
    {
        var start = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["bicep", "build", "--file", Path.Join(TestPaths.RepositoryRoot, "deploy", "infra", file), "--stdout"])
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

    /// <summary>One resource as the parameters deploy it: its type, its name and its properties, every expression worked out.</summary>
    public sealed record Deployed(string Type, string Name, Dictionary<string, object?> Properties);

    /// <summary>
    /// Everything the parameters deploy: each resource whose condition holds, once, or once for every turn of its
    /// copy loop (the condition is then asked for every turn).
    /// </summary>
    public List<Deployed> Deploy()
    {
        var deployed = new List<Deployed>();
        foreach (var resource in Resources)
        {
            var turns = resource.TryGetProperty("copy", out var copy) ? (long)Evaluate(copy.GetProperty("count"))! : (long?)null;
            for (var turn = 0L; turn < (turns ?? 1); turn++)
            {
                var one = turns is null ? this : new CompiledTemplate(_template, Diagnostics, _parameters, _outer, _given, turn);
                if (resource.TryGetProperty("condition", out var condition) && !(bool)one.Evaluate(condition)!)
                {
                    continue;
                }

                var properties = resource.TryGetProperty("properties", out var given) ? one.Evaluate(given) : null;
                deployed.Add(new Deployed(
                    resource.GetProperty("type").GetString()!,
                    (string)one.Evaluate(resource.GetProperty("name"))!,
                    properties as Dictionary<string, object?> ?? []));
            }
        }

        return deployed;
    }

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
            case JsonValueKind.Object:
                Assert.False(value.TryGetProperty("copy", out _), $"A loop inside a resource's properties is not worked out by this test: {value}");
                return value.EnumerateObject().ToDictionary(property => property.Name, property => Evaluate(property.Value), StringComparer.Ordinal);
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
        var result = Call(function, arguments, text);

        // What follows a call: an item of a list ([0]) or a property of an object (.name).
        while (text[position] is '[' or '.')
        {
            if (text[position] == '.')
            {
                var from = ++position;
                while (char.IsAsciiLetterOrDigit(text[position]))
                {
                    position++;
                }

                result = ((Dictionary<string, object?>)result!)[text[from..position]];
            }
            else
            {
                position++;
                var index = Expression(text, ref position);
                SkipSpaces(text, ref position);
                Assert.True(text[position] == ']', $"Expected ] at {position} in {text}");
                position++;
                result = index is string key ? ((Dictionary<string, object?>)result!)[key] : ((List<object?>)result!)[checked((int)(long)index!)];
            }
        }

        return result;
    }

    private object? Call(string function, List<object?> arguments, string text)
    {
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
            "createObject" => Enumerable.Range(0, arguments.Count / 2).ToDictionary(pair => (string)arguments[pair * 2]!, pair => arguments[(pair * 2) + 1], StringComparer.Ordinal),
            "equals" => Equals(arguments[0], arguments[1]),
            "contains" => arguments[0] switch
            {
                List<object?> list => list.Contains(arguments[1]),
                string within => within.Contains((string)arguments[1]!, StringComparison.Ordinal),
                Dictionary<string, object?> named => named.ContainsKey((string)arguments[1]!),
                _ => throw new NotSupportedException($"contains() of {arguments[0]} is not worked out: {text}"),
            },
            "format" => string.Format(CultureInfo.InvariantCulture, (string)arguments[0]!, [.. arguments.Skip(1)]),
            "add" => (long)arguments[0]! + (long)arguments[1]!,
            "less" => (long)arguments[0]! < (long)arguments[1]!,
            "copyIndex" => _copyIndex ?? throw new InvalidOperationException($"copyIndex() outside a copy loop: {text}"),
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
