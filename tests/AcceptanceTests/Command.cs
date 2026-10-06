using System.Diagnostics;
using System.Text;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>Runs a command-line tool (<c>dotnet</c>, <c>docker</c>) to completion and fails with its output.</summary>
internal static class Command
{
    /// <summary>Returns standard output, trimmed. Arguments are passed one by one, so none needs quoting.</summary>
    public static async Task<string> RunAsync(string file, params string[] arguments)
    {
        var result = await TryRunAsync(file, environment: null, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} failed ({result.ExitCode}):\n{result.Output}\n{result.Error}");
        }

        return result.Output.Trim();
    }

    /// <summary>Runs to completion whatever the exit code, with extra environment variables.</summary>
    public static async Task<CommandResult> TryRunAsync(string file, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = new StringBuilder();
        var error = new StringBuilder();
        var streams = Task.WhenAll(CopyAsync(process.StandardOutput, output), CopyAsync(process.StandardError, error));
        await process.WaitForExitAsync();

        // The exit ends the command, not the end of its streams: dotnet leaves build servers running that inherit the
        // pipes and keep them open. A tool without such children ends its streams as it exits.
        await Task.WhenAny(streams, Task.Delay(TimeSpan.FromSeconds(5)));
        return new CommandResult(process.ExitCode, Text(output), Text(error));
    }

    private static async Task CopyAsync(StreamReader reader, StringBuilder text)
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                lock (text)
                {
                    text.Append(buffer, 0, read);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The command ended and its process was disposed while a child still held the pipe open.
        }
    }

    private static string Text(StringBuilder text)
    {
        lock (text)
        {
            return text.ToString();
        }
    }
}

/// <summary>How a command ended and what it wrote.</summary>
internal sealed record CommandResult(int ExitCode, string Output, string Error);
