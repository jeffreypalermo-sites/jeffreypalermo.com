using System.Globalization;
using System.Text.RegularExpressions;
using JeffreyPalermo.UnitTests.Architecture;

namespace JeffreyPalermo.UnitTests.UI;

/// <summary>
/// The site's look is one stylesheet: "Masthead", in Clear Measure's colours (ADR-0019). These tests read the file
/// and hold what Jeffrey asked of the look, as far as a stylesheet can break it by itself: nothing moves, nothing is
/// fixed or sticky, nothing is fetched but the site's two font files, text stands out from its ground, the keyboard's
/// focus is drawn, search stands above the posts on a phone, and the file stays small. <c>SiteInABrowserTests</c>
/// holds the same in a browser, against the container.
/// </summary>
public partial class StylesheetTests
{
    /// <summary>WCAG AA for text, and for what is not text (a focus ring, the edge of a field).</summary>
    private const double Text = 4.5;
    private const double NotText = 3.0;

    private static readonly string Root = DependencyRuleTests.RepositoryRoot();

    private static readonly string Source = File.ReadAllText(Path.Join(Root, "src", "UI.Server", "wwwroot", "_assets", "site.css"));

    private static readonly string Decision = File.ReadAllText(Path.Join(Root, "docs", "adr", "0019-the-masthead-look.md"));

    private static readonly IReadOnlyList<Rule> Rules = Parse(Comment().Replace(Source, string.Empty), media: null).ToList();

    private static readonly Dictionary<string, string> Tokens = Rules
        .Where(rule => rule.Media is null && rule.Selectors.Contains(":root"))
        .SelectMany(rule => rule.Declarations)
        .Where(declaration => declaration.Property.StartsWith("--", StringComparison.Ordinal))
        .ToDictionary(declaration => declaration.Property, declaration => declaration.Value);

    private static IEnumerable<(Rule Rule, string Property, string Value)> Declarations =>
        Rules.SelectMany(rule => rule.Declarations.Select(declaration => (rule, declaration.Property, declaration.Value)));

    [Fact]
    public void NothingMoves()
    {
        Assert.DoesNotContain(Rules, rule => rule.Selectors[0].StartsWith("@keyframes", StringComparison.Ordinal));
        Assert.All(
            Declarations.Where(d => d.Property.StartsWith("transition", StringComparison.Ordinal) || d.Property.StartsWith("animation", StringComparison.Ordinal)),
            d => Assert.Equal("none !important", d.Value));
        Assert.DoesNotContain(Declarations, d => d.Property is "transform" or "translate" or "rotate" or "scale" or "will-change" or "offset-path" or "view-transition-name");
        Assert.All(Declarations.Where(d => d.Property == "scroll-behavior"), d => Assert.Equal("auto", d.Value));

        // The stylesheet also takes motion away from whatever an old post body brings with it.
        var everything = Assert.Single(Rules, rule => rule.Media is null && rule.Selectors.SequenceEqual(["*", "*::before", "*::after"]));
        Assert.Contains(("animation", "none !important"), everything.Declarations);
        Assert.Contains(("transition", "none !important"), everything.Declarations);
        Assert.Contains(("scroll-behavior", "auto"), Assert.Single(Rules, rule => rule.Media is null && rule.Selectors.SequenceEqual(["html"])).Declarations);
    }

    [Fact]
    public void NothingIsFixedOrSticky()
    {
        var positions = Declarations.Where(d => d.Property == "position").Select(d => d.Value).Distinct().ToList();

        // "absolute" is the skip link and the text for screen readers: out of the way, not over the page.
        Assert.Equal(["absolute"], positions);
        Assert.Equal(
            [".screen-reader-text", ".skip-link"],
            Declarations.Where(d => d.Property == "position").SelectMany(d => d.Rule.Selectors).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Declarations, d => d.Property == "background-attachment");
    }

    [Fact]
    public void NothingIsFetchedButTheSitesTwoFontFiles()
    {
        var css = Comment().Replace(Source, string.Empty);

        Assert.DoesNotContain("@import", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//", css, StringComparison.Ordinal);
        Assert.DoesNotContain("data:", css, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["fonts/noto-serif-latin.woff2", "fonts/noto-serif-latin-italic.woff2"],
            Url().Matches(css).Select(match => match.Groups["url"].Value));
        Assert.All(Url().Matches(css), match => Assert.True(
            File.Exists(Path.Join(Root, "src", "UI.Server", "wwwroot", "_assets", match.Groups["url"].Value)), $"{match.Groups["url"].Value} is not a file of the site."));

        // No picture: a background is a colour.
        Assert.DoesNotContain(Declarations, d => d.Property is "background-image" or "border-image" or "mask" or "mask-image" or "list-style-image" or "cursor" && d.Value.Contains("url(", StringComparison.Ordinal));
        Assert.All(Rules.Where(rule => rule.Selectors[0] == "@font-face"), face => Assert.Contains(("font-display", "swap"), face.Declarations));
        Assert.Equal(2, Rules.Count(rule => rule.Selectors[0] == "@font-face"));
    }

    /// <summary>The file a reader waits for before the first page is drawn. The WordPress look's was 14,056 bytes.</summary>
    [Fact]
    public void TheStylesheetStaysSmall()
    {
        var bytes = new FileInfo(Path.Join(Root, "src", "UI.Server", "wwwroot", "_assets", "site.css")).Length;

        Assert.True(bytes < 28_000, $"site.css is {bytes} bytes. ADR-0019 took 23 KB as the price of the look; a larger file is a decision.");
    }

    /// <summary>
    /// Text on its ground, by the rules that colour them. A name that starts with two dashes is a colour of the
    /// palette; anything else is a selector, whose <c>color</c> is the text and whose <c>background</c> is the ground.
    /// </summary>
    [Theory]
    // Reading: body text, links (also while pointed at), headings, datelines.
    [InlineData("body", "body")]
    [InlineData("a", "body")]
    [InlineData("a:hover", "body")]
    [InlineData(".entry-title", "body")]
    [InlineData(".entry-title a:hover", "body")]
    [InlineData(".entry-meta", "body")]
    [InlineData(".entry-author", "body")]
    [InlineData(".page-title", "body")]
    [InlineData(".page-title span", "body")]
    [InlineData(".widget-title", "body")]
    [InlineData(".profile-name", "body")]
    [InlineData(".comment-date", "body")]
    [InlineData(".no-comments", "body")]
    [InlineData(".entry-content blockquote", "body")]
    // The masthead and the footer.
    [InlineData(".site-title a", ".site-header")]
    [InlineData(".site-title a:hover", ".site-header")]
    [InlineData(".site-description", ".site-header")]
    [InlineData(".site-menu a", ".site-header")]
    [InlineData(".site-menu a[aria-current=\"page\"]", ".site-header")]
    [InlineData(".site-menu a:hover", ".site-menu a:hover")]
    [InlineData(".site-menu a[aria-current=\"page\"]:hover", ".site-menu a:hover")]
    [InlineData(".site-footer", ".site-footer")]
    [InlineData(".site-info a", ".site-footer")]
    // Search, tags, the skip link.
    [InlineData(".search-form input", ".search-form input")]
    [InlineData(".search-form button", ".search-form button")]
    [InlineData(".search-form button:hover", ".search-form button:hover")]
    [InlineData(".tagcloud a", "body")]
    [InlineData(".tagcloud a:hover", ".tagcloud a:hover")]
    [InlineData(".skip-link:focus", ".skip-link:focus")]
    // Code: every colour on the block's ground and on the stripe of every second line.
    [InlineData("--code-ink", "--code-bg")]
    [InlineData("--code-ink", "--code-alt")]
    [InlineData("--code-num", "--code-bg")]
    [InlineData("--code-num", "--code-alt")]
    [InlineData("--code-kw", "--code-bg")]
    [InlineData("--code-kw", "--code-alt")]
    [InlineData("--code-str", "--code-bg")]
    [InlineData("--code-str", "--code-alt")]
    [InlineData("--code-rem", "--code-bg")]
    [InlineData("--code-rem", "--code-alt")]
    [InlineData("--code-tag", "--code-bg")]
    [InlineData("--code-tag", "--code-alt")]
    public void TextStandsOutFromItsGround(string text, string ground)
    {
        var ratio = Contrast(ColourOf(text, "color"), ColourOf(ground, "background"));

        Assert.True(ratio >= Text, $"{text} on {ground} is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1; text needs {Text}:1.");
    }

    /// <summary>The figures the decision record gives for body text and for links are the stylesheet's.</summary>
    [Fact]
    public void BodyTextAndLinksHaveTheContrastTheDecisionRecords()
    {
        var body = Contrast(ColourOf("body", "color"), ColourOf("body", "background"));
        var links = Contrast(ColourOf("a", "color"), ColourOf("body", "background"));

        Assert.Equal(15.9, body, tolerance: 0.05);
        Assert.Equal(8.9, links, tolerance: 0.05);
        Assert.Contains("15.9:1", Decision, StringComparison.Ordinal);
        Assert.Contains("8.9:1", Decision, StringComparison.Ordinal);

        // Clear Measure's primary blue is too faint for small text on white: it colours rules and rings only.
        Assert.True(Contrast(Tokens["--cm-blue"], "#FFFFFF") < Text);
        Assert.DoesNotContain(Declarations, d => d.Property == "color" && d.Value.Contains("--cm-blue", StringComparison.Ordinal));
        Assert.DoesNotContain(Tokens, token => token.Key is "--ink" or "--ink-strong" or "--muted" or "--link" or "--link-hover" or "--label" && token.Value.Contains("--cm-blue", StringComparison.Ordinal));
    }

    [Fact]
    public void TheKeyboardsFocusIsARingThatStandsOutWhereverItIs()
    {
        var focus = Assert.Single(Rules, rule => rule.Selectors.Contains("a:focus-visible"));

        Assert.Equal(["a:focus-visible", "button:focus-visible", "input:focus-visible"], focus.Selectors);
        Assert.Contains(("outline", "3px solid var(--focus)"), focus.Declarations);

        // On the page the ring is blue; the masthead and the footer are navy and give it the yellow.
        Assert.True(Contrast(Tokens["--focus"], Tokens["--bg"]) >= NotText);
        foreach (var band in new[] { ".site-header", ".site-footer" })
        {
            var ring = Resolve(Single(band, "--focus"));
            Assert.True(Contrast(ring, ColourOf(band, "background")) >= NotText, $"The focus ring cannot be seen on {band}.");
        }

        // Nothing takes the ring away, except from the main column, which takes the focus only from the skip link.
        Assert.Equal(
            [".content-area:focus"],
            Declarations.Where(d => d.Property == "outline" && d.Value is "none" or "0").SelectMany(d => d.Rule.Selectors));
        Assert.DoesNotContain(Declarations, d => d.Property is "outline-style" or "outline-width" or "outline-color");
    }

    /// <summary>
    /// Where the reader is: the menu entry of the page being read is marked by more than its colour, and a link in a
    /// post is underlined.
    /// </summary>
    [Fact]
    public void TheCurrentMenuEntryAndTheLinksOfAPostAreMarkedByMoreThanColour()
    {
        Assert.Equal("inset 0 -5px 0 var(--cm-yellow)", Single(".site-menu a[aria-current=\"page\"]", "box-shadow"));
        Assert.Equal("underline", Single("a", "text-decoration"));
        Assert.DoesNotContain(Rules, rule => rule.Selectors.Any(selector => selector.StartsWith(".entry-content a", StringComparison.Ordinal))
            && rule.Declarations.Any(d => d.Property == "text-decoration" && d.Value == "none"));
    }

    /// <summary>
    /// On a narrow screen the sidebar's boxes take their own places in the page's column, so that the search box
    /// stands between the masthead and the posts and the rest of the index follows the posts.
    /// </summary>
    [Fact]
    public void OnANarrowScreenSearchStandsAboveThePostsAndTheRestOfTheIndexBelow()
    {
        var narrow = Rules.Where(rule => rule.Media == "(max-width: 62rem)").ToList();
        int Order(string selector) => int.Parse(
            Assert.Single(narrow, rule => rule.Selectors.SequenceEqual([selector])).Declarations.Single(d => d.Property == "order").Value, CultureInfo.InvariantCulture);

        Assert.Contains(("display", "flex"), Assert.Single(narrow, rule => rule.Selectors.SequenceEqual([".site"])).Declarations);
        Assert.Contains(("flex-direction", "column"), Assert.Single(narrow, rule => rule.Selectors.SequenceEqual([".site"])).Declarations);
        Assert.Contains(("display", "contents"), Assert.Single(narrow, rule => rule.Selectors.SequenceEqual([".widget-area"])).Declarations);
        Assert.True(Order(".widget-search") < Order(".content-area"));
        Assert.True(Order(".content-area") < Order(".widget"));
        Assert.True(Order(".widget") < Order(".site-footer"));

        // The header has no order of its own (0), so it comes first; nothing is hidden but the label of the search box.
        Assert.DoesNotContain(Declarations, d => d.Property == "order" && d.Rule.Selectors.Contains(".site-header"));
        Assert.Equal(
            [".widget-search .widget-title"],
            narrow.Where(rule => rule.Declarations.Contains(("display", "none"))).SelectMany(rule => rule.Selectors));
    }

    /// <summary>A long line of code scrolls inside its block, whatever a post's own style attribute says.</summary>
    [Fact]
    public void CodeScrollsInsideItsOwnBlock()
    {
        Assert.Equal("auto !important", Single(".entry-content pre", "overflow"));
        Assert.Equal("100%", Single(".entry-content pre", "max-width"));
        Assert.Equal("auto", Single(".entry-content div.csharpcode", "overflow-x"));
        Assert.Equal("auto", Single(".entry-content", "overflow-x"));
        Assert.Equal("100%", Single("img", "max-width"));
    }

    /// <summary>The colours named <c>--cm-*</c> are Clear Measure's, and the decision record says where each was read.</summary>
    [Fact]
    public void TheColoursOfClearMeasureAreTheOnesTheDecisionRecords()
    {
        var brand = Tokens.Where(token => token.Key.StartsWith("--cm-", StringComparison.Ordinal)).ToDictionary(token => token.Key, token => token.Value);

        Assert.Equal("#004B87", brand["--cm-navy"]);
        Assert.Equal("#0085CA", brand["--cm-blue"]);
        Assert.Equal("#EECB1A", brand["--cm-yellow"]);
        Assert.All(brand, colour => Assert.Contains($"`{colour.Value}`", Decision, StringComparison.Ordinal));

        // Every token is used: a colour nobody reads is not part of the look.
        var css = Comment().Replace(Source, string.Empty);
        Assert.All(Tokens.Keys, token => Assert.True(css.Contains($"var({token})", StringComparison.Ordinal), $"{token} is defined and never used."));
    }

    /// <summary>The decision record names the tests that hold each thing Jeffrey asked of the look. Each of them exists.</summary>
    [Fact]
    public void EveryTestTheDecisionNamesExists()
    {
        var asked = Decision[Decision.IndexOf("### What Jeffrey asked for", StringComparison.Ordinal)..Decision.IndexOf("## Options that were not taken", StringComparison.Ordinal)];
        var named = TestName().Matches(asked).Select(match => match.Groups["name"].Value).Where(name => !name.EndsWith("Tests", StringComparison.Ordinal)).Distinct().ToList();
        var tests = string.Concat(new[]
        {
            Path.Join(Root, "tests", "UnitTests", "UI", "StylesheetTests.cs"),
            Path.Join(Root, "tests", "IntegrationTests", "SitePagesTests.cs"),
            Path.Join(Root, "tests", "AcceptanceTests", "SiteInABrowserTests.cs"),
        }.Select(File.ReadAllText));

        Assert.True(named.Count >= 20, $"Only {named.Count} tests were read from the decision record.");
        Assert.All(named, name => Assert.True(Regex.IsMatch(tests, $@"public (async Task|void) {name}\("), $"ADR-0019 names {name}, which is not a test."));
        foreach (var asking in new[] { "**Pop**", "**Snappy**", "**Easy to navigate**", "**No unnecessary or annoying animations**" })
        {
            Assert.Contains($"| {asking} |", asked, StringComparison.Ordinal);
        }
    }

    private static string Single(string selector, string property)
    {
        var values = Rules
            .Where(rule => rule.Media is null && rule.Selectors.Contains(selector))
            .SelectMany(rule => rule.Declarations)
            .Where(declaration => declaration.Property == property)
            .Select(declaration => declaration.Value)
            .ToList();
        Assert.True(values.Count == 1, $"{selector} has {values.Count} declarations of {property}; the test expects one.");
        return values[0];
    }

    /// <summary>A colour of the palette by its name, or the colour a selector's rule gives a property.</summary>
    private static string ColourOf(string name, string property) =>
        name.StartsWith("--", StringComparison.Ordinal) ? Resolve($"var({name})") : Resolve(Single(name, property));

    private static string Resolve(string value)
    {
        value = value.Replace(" !important", string.Empty, StringComparison.Ordinal).Trim();
        for (var depth = 0; depth < 8 && Variable().Match(value) is { Success: true } variable; depth++)
        {
            Assert.True(Tokens.TryGetValue(variable.Groups["name"].Value, out var token), $"{variable.Groups["name"].Value} is not a colour of the palette.");
            value = token!;
        }

        Assert.Matches("^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$", value);
        return value.Length == 4 ? $"#{value[1]}{value[1]}{value[2]}{value[2]}{value[3]}{value[3]}" : value;
    }

    /// <summary>The WCAG contrast ratio of two opaque colours.</summary>
    private static double Contrast(string one, string other)
    {
        var (lighter, darker) = (Math.Max(Luminance(Resolve(one)), Luminance(Resolve(other))), Math.Min(Luminance(Resolve(one)), Luminance(Resolve(other))));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string hex)
    {
        static double Channel(string pair)
        {
            var value = int.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(hex[1..3])) + (0.7152 * Channel(hex[3..5])) + (0.0722 * Channel(hex[5..7]));
    }

    /// <summary>The rules of a stylesheet without comments, those inside <c>@media</c> with their condition.</summary>
    private static IEnumerable<Rule> Parse(string css, string? media)
    {
        var position = 0;
        while (position < css.Length)
        {
            var open = css.IndexOf('{', position);
            if (open < 0)
            {
                yield break;
            }

            var prelude = Space().Replace(css[position..open], " ").Trim();
            var close = open;
            for (var depth = 1; depth > 0;)
            {
                close = css.IndexOfAny(['{', '}'], close + 1);
                depth += css[close] == '{' ? 1 : -1;
            }

            var block = css[(open + 1)..close];
            position = close + 1;
            if (prelude.StartsWith("@media", StringComparison.Ordinal))
            {
                foreach (var rule in Parse(block, prelude["@media".Length..].Trim()))
                {
                    yield return rule;
                }

                continue;
            }

            var declarations = block.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(declaration => declaration.Split(':', 2, StringSplitOptions.TrimEntries))
                .Select(parts => (parts[0], Space().Replace(parts[1], " ")))
                .ToList();
            yield return new Rule(media, prelude.Split(',', StringSplitOptions.TrimEntries), declarations);
        }
    }

    private sealed record Rule(string? Media, IReadOnlyList<string> Selectors, IReadOnlyList<(string Property, string Value)> Declarations);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    [GeneratedRegex(@"url\(\s*[""']?(?<url>[^""')]+)[""']?\s*\)")]
    private static partial Regex Url();

    [GeneratedRegex(@"var\((?<name>--[a-z0-9-]+)\)")]
    private static partial Regex Variable();

    [GeneratedRegex("`(?<name>[A-Z][A-Za-z]+)`")]
    private static partial Regex TestName();
}
