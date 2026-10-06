namespace JeffreyPalermo.Core.Content;

/// <summary>Which posts an archive page lists. WordPress-compatible: dates are local publish dates.</summary>
public sealed record ArchiveFilter
{
    public static ArchiveFilter All { get; } = new();

    public int? Year { get; private init; }
    public int? Month { get; private init; }
    public int? Day { get; private init; }
    public string? Taxonomy { get; private init; }
    public string? TermSlug { get; private init; }

    public static ArchiveFilter ForDate(int year, int? month = null, int? day = null)
    {
        if (day is not null && month is null)
        {
            throw new ArgumentException("A day archive needs a month.", nameof(day));
        }

        return new() { Year = year, Month = month, Day = day };
    }

    public static ArchiveFilter ForTerm(string taxonomy, string slug) => new() { Taxonomy = taxonomy, TermSlug = slug };

    public bool Matches(Post post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return (Year is null || post.Published.Year == Year)
            && (Month is null || post.Published.Month == Month)
            && (Day is null || post.Published.Day == Day)
            && Taxonomy switch
            {
                null => true,
                Taxonomies.Category => post.CategorySlugs.Contains(TermSlug),
                Taxonomies.Tag => post.TagSlugs.Contains(TermSlug),
                Taxonomies.Author => post.AuthorSlug == TermSlug,
                _ => false,
            };
    }
}

public sealed record PagedList<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalItems)
{
    public int TotalPages => (TotalItems + PageSize - 1) / PageSize;
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}
