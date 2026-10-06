using System.Diagnostics;
using System.Text;

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
        var output = new StringBuilder();
        var error = new StringBuilder();
        var streams = Task.WhenAll(CopyAsync(process.StandardOutput, output), CopyAsync(process.StandardError, error));
        await process.WaitForExitAsync();

        // The exit ends the command, not the end of its streams: dotnet leaves build servers running that inherit the
        // pipes and keep them open. A tool without such children ends its streams as it exits.
        await Task.WhenAny(streams, Task.Delay(TimeSpan.FromSeconds(5)));
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {string.Join(' ', arguments)} failed ({process.ExitCode}):\n{Text(output)}\n{Text(error)}");
        }

        return Text(output).Trim();
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
