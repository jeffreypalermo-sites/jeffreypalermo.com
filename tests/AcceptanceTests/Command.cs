using System.Diagnostics;

namespace JeffreyPalermo.AcceptanceTests;

/// <summary>Runs a command-line tool (<c>dotnet</c>, <c>docker</c>) to completion and fails with its output.</summary>
internal static class Command
{
    /// <summary>Returns standard output, trimmed. Arguments are passed one by one, so none needs quoting.</summary>
    public static async Task<string> RunAsync(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} failed ({process.ExitCode}):\n{await output}\n{await error}");
        }

        return (await output).Trim();
    }
}
