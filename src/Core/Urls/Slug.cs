using System.Text;

namespace JeffreyPalermo.Core.Urls;

public static class Slug
{
    /// <summary>
    /// Approximates WordPress <c>sanitize_title_with_dashes</c>, so Graffiti-era slugs such as
    /// <c>getting-started-with-the-asp.net-mvc-framework</c> match today's <c>…-asp-net-mvc-framework</c>.
    /// Quotes are dropped; any other run of non-alphanumerics becomes a single dash.
    /// </summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var decoded = Uri.UnescapeDataString(value).ToLowerInvariant();
        var builder = new StringBuilder(decoded.Length);
        var pendingDash = false;
        foreach (var c in decoded)
        {
            if (c is '\'' or '"' or '’' or '‘' or '“' or '”')
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(c);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.ToString();
    }
}
