using System.Globalization;

namespace JeffreyPalermo.UI.Server.Presentation;

/// <summary>Dates and counts worded as the WordPress theme worded them.</summary>
public static class DisplayText
{
    /// <summary><c>4:09 pm on January 9, 2020</c>.</summary>
    public static string Moment(DateTime local) =>
        string.Create(CultureInfo.InvariantCulture, $"{local:h:mm} {(local.Hour < 12 ? "am" : "pm")} on {local:MMMM d, yyyy}");

    /// <summary><c>January 2020</c>.</summary>
    public static string Month(int year, int month) => new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary><c>July 29, 2008</c>.</summary>
    public static string Day(int year, int month, int day) => new DateTime(year, month, day).ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    /// <summary>The value of a <c>datetime</c> attribute; UTC times say so.</summary>
    public static string Machine(DateTime moment) =>
        moment.ToString(moment.Kind == DateTimeKind.Utc ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A document title in WordPress's order: the most specific part first, the site's name last.</summary>
    public static string Title(params string[] parts) => string.Join(" | ", parts);

    /// <summary><c>1 comment</c>, <c>12 comments</c>.</summary>
    public static string Count(int count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural)}");
}
