using System.Diagnostics;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <summary>
/// Fetches a video's own picture from YouTube's picture host, once, when its frame is first written: the poster
/// is a file of this site from then on (ADR-0017). Both pictures asked for are 16 to 9. The large one (1280 by
/// 720, about 140 KB) is made small by ImageMagick when the machine has it: 640 by 360, 25 KB or less. The
/// picture of an episode that was recorded as sound alone is the show's square mark between two black bars: the
/// bars are cut off and the mark stands on the look's navy instead. Without ImageMagick, or for a video that has
/// no large picture, the small one is kept as it comes (320 by 180, about 12 KB).
/// </summary>
public sealed class PodcastPosters(HttpClient http, TimeSpan pause)
{
    private static readonly string[] Encoders = ["magick", "convert"];

    public async Task<byte[]?> FetchAsync(string id, CancellationToken cancellationToken)
    {
        if (await GetAsync($"https://i.ytimg.com/vi/{id}/maxresdefault.jpg", cancellationToken).ConfigureAwait(false) is { } large
            && await MakeSmallAsync(large, cancellationToken).ConfigureAwait(false) is { } small)
        {
            return small;
        }

        return await GetAsync($"https://i.ytimg.com/vi/{id}/mqdefault.jpg", cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> GetAsync(string address, CancellationToken cancellationToken)
    {
        await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await http.GetAsync(new Uri(address), cancellationToken).ConfigureAwait(false);
            var bytes = response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : null;
            // A JPEG, whatever the host calls it.
            return bytes is [0xFF, 0xD8, 0xFF, ..] ? bytes : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>The picture at 640 by 360, black bars replaced by navy, as a JPEG without its metadata; null when no encoder is there or it fails.</summary>
    internal static async Task<byte[]?> MakeSmallAsync(byte[] jpeg, CancellationToken cancellationToken)
    {
        foreach (var encoder in Encoders)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(encoder, ["jpg:-", "-bordercolor", "black", "-border", "1", "-fuzz", "6%", "-trim", "+repage", "-resize", "640x360", "-background", "#004B87", "-gravity", "center", "-extent", "640x360", "-strip", "-interlace", "Plane", "-quality", "60", "jpg:-"])
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                })!;
                using var output = new MemoryStream();
                var reading = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
                var errors = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.StandardInput.BaseStream.WriteAsync(jpeg, cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
                await reading.ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                if (process.ExitCode == 0 && output.ToArray() is [0xFF, 0xD8, 0xFF, ..] small)
                {
                    return small;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                // Not on this machine, or it would not read the picture: the next one, or the small picture as it comes.
            }
        }

        return null;
    }
}
