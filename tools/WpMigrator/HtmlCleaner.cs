using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <param name="Html">The cleaned markup.</param>
/// <param name="UploadPaths">Every <c>/wp-content/uploads/</c> path the markup refers to.</param>
/// <param name="ExternalImages">The upload paths that are copies of images on other hosts, with where to fetch each.</param>
public sealed record CleanedHtml(string Html, IReadOnlySet<string> UploadPaths, IReadOnlyDictionary<string, IReadOnlyList<string>> ExternalImages);

/// <summary>
/// Normalizes 20 years of WordPress, Graffiti, and Word-pasted markup into clean HTML while keeping the content:
/// unwraps <c>&lt;font&gt;</c>, drops Office (<c>o:p</c>) elements and <c>mso-*</c> styling, removes Jetpack
/// <c>data-*</c>/<c>srcset</c> attributes, deletes empty spacer paragraphs, rewrites links, and points images hosted
/// elsewhere at their local copies (<see cref="ExternalImage"/>).
/// </summary>
public sealed class HtmlCleaner(LinkRewriter links)
{
    private const string UploadsPrefix = "/wp-content/uploads/";

    private static readonly HtmlParser Parser = new();

    private static readonly HashSet<string> DroppedStyleProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "font-family", "font-size", "line-height", "margin", "margin-top", "margin-bottom", "margin-left", "margin-right",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "ul", "ol", "table", "pre", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "figure", "hr",
    };

    private static readonly string[] MeaningfulEmptyContent = ["img", "iframe", "object", "embed", "video", "audio", "table", "hr", "input", "script"];

    public CleanedHtml Clean(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = Parser.ParseDocument("<!doctype html><html><body><div id=\"root\"></div></body></html>");
        var root = document.GetElementById("root")!;
        root.InnerHtml = html;

        foreach (var officeElement in root.QuerySelectorAll("*").Where(e => e.LocalName.Contains(':', StringComparison.Ordinal)).ToList())
        {
            officeElement.Remove();
        }

        foreach (var font in root.QuerySelectorAll("font").ToList())
        {
            Unwrap(font);
        }

        var uploads = new HashSet<string>(StringComparer.Ordinal);
        var externalImages = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var element in root.QuerySelectorAll("*"))
        {
            CleanAttributes(element, uploads, externalImages);
        }

        foreach (var paragraph in root.QuerySelectorAll("p").Reverse().ToList())
        {
            if (IsEmpty(paragraph))
            {
                paragraph.Remove();
            }
        }

        foreach (var lineBreak in root.Children.Where(e => e.LocalName == "br").ToList())
        {
            if (lineBreak.PreviousElementSibling is { } previous && BlockElements.Contains(previous.LocalName))
            {
                lineBreak.Remove();
            }
        }

        return new CleanedHtml(root.InnerHtml.Trim(), uploads, externalImages);
    }

    private void CleanAttributes(IElement element, HashSet<string> uploads, Dictionary<string, IReadOnlyList<string>> externalImages)
    {
        foreach (var attribute in element.Attributes.ToList())
        {
            var name = attribute.Name;
            if (name.StartsWith("data-", StringComparison.OrdinalIgnoreCase) || name is "srcset" or "sizes")
            {
                element.RemoveAttribute(name);
            }
        }

        foreach (var urlAttribute in (string[])["href", "src"])
        {
            var value = element.GetAttribute(urlAttribute);
            if (value is null)
            {
                continue;
            }

            var rewritten = links.Rewrite(value);
            if (urlAttribute == "src" && element.LocalName == "img" && ExternalImage.From(rewritten) is { } image)
            {
                rewritten = image.LocalPath;
                externalImages.TryAdd(image.LocalPath, image.Sources);
            }

            element.SetAttribute(urlAttribute, rewritten);
            if (rewritten.StartsWith(UploadsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                uploads.Add(StripQueryAndFragment(rewritten));
            }
        }

        var classes = element.ClassList.Where(c => c.StartsWith("Mso", StringComparison.OrdinalIgnoreCase)).ToList();
        if (classes.Count > 0)
        {
            element.ClassList.Remove([.. classes]);
        }

        if (element.ClassList.Length == 0)
        {
            element.RemoveAttribute("class");
        }

        var style = element.GetAttribute("style");
        if (style is not null)
        {
            var kept = style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(declaration =>
                {
                    var property = declaration.Split(':', 2)[0].Trim();
                    return !property.StartsWith("mso-", StringComparison.OrdinalIgnoreCase) && !DroppedStyleProperties.Contains(property);
                })
                .ToList();

            if (kept.Count == 0)
            {
                element.RemoveAttribute("style");
            }
            else
            {
                element.SetAttribute("style", string.Join("; ", kept));
            }
        }
    }

    private static bool IsEmpty(IElement paragraph) =>
        string.IsNullOrWhiteSpace(paragraph.TextContent.Replace(' ', ' '))
        && paragraph.QuerySelectorAll(string.Join(',', MeaningfulEmptyContent)).Length == 0;

    private static void Unwrap(IElement element)
    {
        var parent = element.Parent!;
        while (element.FirstChild is { } child)
        {
            parent.InsertBefore(child, element);
        }

        element.Remove();
    }

    private static string StripQueryAndFragment(string url)
    {
        var end = url.IndexOfAny(['?', '#']);
        return end < 0 ? url : url[..end];
    }
}
