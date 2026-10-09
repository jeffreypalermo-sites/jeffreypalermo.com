using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Markdig;

namespace JeffreyPalermo.Tools.WpMigrator;

/// <param name="Markdown">The notes as the body of a Markdown post.</param>
/// <param name="Text">The notes as plain text, one space between words: what an excerpt is cut from.</param>
/// <param name="BlocksKeptAsHtml">How many paragraphs or lists are written as HTML because Markdown would not give the same text and links back.</param>
/// <param name="Removed">What was taken out because a body may not hold it: the element's name, and its address when it has one.</param>
public sealed record CleanedShowNotes(string Markdown, string Text, int BlocksKeptAsHtml, IReadOnlyList<string> Removed);

/// <summary>
/// Turns an episode's show notes, markup pasted into the show's host from a word processor, into Markdown that says
/// the same: the words, what is bold or italic, the links, the line breaks and the lists. Everything else goes:
/// <c>span</c>s and their styles, classes, empty paragraphs, and whatever a body of this site may not hold (scripts,
/// frames, pictures and recordings on other hosts). Nothing is added and nothing is reworded.
/// </summary>
/// <remarks>
/// Each paragraph and each list is checked: its Markdown is rendered as the site renders it and read again. When
/// that does not give the same text, emphasis and links (text beside emphasis that Markdown reads otherwise, an
/// address Markdown would escape), the block is written as HTML instead, which Markdown leaves as it is.
/// </remarks>
public static partial class ShowNotes
{
    /// <summary>As the site renders a Markdown post (<c>FileSystemContentSource</c>).</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private static readonly HtmlParser Parser = new();

    private static readonly System.Buffers.SearchValues<char> RomanFigures = System.Buffers.SearchValues.Create("ivxlcdmIVXLCDM");

    /// <summary>What a body may not hold, or what means nothing without the page it came from.</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "object", "embed", "img", "picture", "source", "video", "audio", "track",
        "link", "meta", "form", "input", "button", "select", "textarea", "svg", "canvas", "noscript", "template",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article", "table", "tr", "td", "th", "li", "pre",
    };

    public static CleanedShowNotes FromHtml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var removed = new List<string>();
        var blocks = Read(html, removed);
        var keptAsHtml = 0;
        var markdown = new StringBuilder();
        foreach (var block in blocks)
        {
            var written = Write(block);
            if (!Read(Markdown.ToHtml(written, Pipeline), []).SequenceEqual([block]))
            {
                written = block.ToHtml();
                keptAsHtml++;
            }

            markdown.Append(written).Append("\n\n");
        }

        var text = string.Join(' ', blocks.Select(block => block.Text).Where(t => t.Length > 0));
        return new CleanedShowNotes(markdown.ToString().TrimEnd('\n'), Spaces().Replace(text, " ").Trim(), keptAsHtml, removed);
    }

    /// <summary>The first words of a text, as WordPress cut an excerpt: 55 words, and <c> […]</c> when there were more.</summary>
    public static string Excerpt(string text, int words = 55)
    {
        ArgumentNullException.ThrowIfNull(text);
        var all = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return all.Length <= words ? string.Join(' ', all) : string.Join(' ', all.Take(words)) + " […]";
    }

    // ---- Reading: markup to paragraphs and lists ----

    private static List<Block> Read(string html, List<string> removed)
    {
        var document = Parser.ParseDocument("<!doctype html><html><body><div id=\"root\"></div></body></html>");
        var root = document.GetElementById("root")!;
        root.InnerHtml = html;
        foreach (var element in root.QuerySelectorAll("*").Where(e => Dropped.Contains(e.LocalName)).ToList())
        {
            if (element.Parent is not null)
            {
                var address = element.GetAttribute("src") ?? element.GetAttribute("href") ?? element.GetAttribute("data");
                removed.Add(address is null ? element.LocalName : $"{element.LocalName} {address}");
                element.Remove();
            }
        }

        var blocks = new List<Block>();
        ReadBlocks(root, default, blocks);
        return blocks;
    }

    private static void ReadBlocks(INode parent, Format format, List<Block> blocks)
    {
        var inline = new List<Piece>();
        void Flush()
        {
            blocks.AddRange(Paragraphs(inline));
            inline.Clear();
        }

        foreach (var node in parent.ChildNodes)
        {
            if (node is IText text)
            {
                inline.Add(new Piece(text.Data, format));
            }
            else if (node is IElement element)
            {
                var name = element.LocalName;
                if (name is "ul" or "ol")
                {
                    Flush();
                    var items = element.Children.Where(child => child.LocalName == "li").Select(item =>
                    {
                        var content = new List<Block>();
                        ReadBlocks(item, format, content);
                        return new ListItem(content);
                    }).Where(item => item.Blocks.Count > 0).ToList();
                    if (items.Count > 0)
                    {
                        blocks.Add(new ListBlock(name == "ol", items));
                    }
                }
                else if (BlockElements.Contains(name))
                {
                    Flush();
                    ReadBlocks(element, format, blocks);
                }
                else if (name == "br")
                {
                    inline.Add(Piece.Break);
                }
                else
                {
                    ReadInline(element, format, inline, blocks, Flush);
                }
            }
        }

        Flush();
    }

    private static void ReadInline(IElement element, Format format, List<Piece> inline, List<Block> blocks, Action flush)
    {
        var inner = element.LocalName switch
        {
            "strong" or "b" => format with { Bold = true },
            "em" or "i" => format with { Italic = true },
            "a" when element.GetAttribute("href")?.Trim() is { Length: > 0 } href => format with { Href = href },
            _ => format,
        };

        // A list or a paragraph inside a span, as word processors nest them: it ends the line it stands in.
        if (element.QuerySelector("p, div, ul, ol, li, blockquote, h1, h2, h3, h4, h5, h6, table, pre") is not null)
        {
            flush();
            ReadBlocks(element, inner, blocks);
            return;
        }

        foreach (var node in element.ChildNodes)
        {
            if (node is IText text)
            {
                inline.Add(new Piece(text.Data, inner));
            }
            else if (node is IElement child)
            {
                if (child.LocalName == "br")
                {
                    inline.Add(Piece.Break);
                }
                else
                {
                    ReadInline(child, inner, inline, blocks, flush);
                }
            }
        }
    }

    /// <summary>
    /// The paragraphs of a run of text: white space as one space, none where a line starts or ends, emphasis taken
    /// off the spaces at its edges, and a new paragraph where two line breaks or more stood.
    /// </summary>
    private static List<Paragraph> Paragraphs(List<Piece> pieces)
    {
        // One character at a time: a space carries no emphasis, and a space outside a link's words is not part of it.
        var characters = new List<(char Character, Format Format)>();
        foreach (var piece in pieces)
        {
            if (piece.IsBreak)
            {
                characters.Add(('\n', default));
                continue;
            }

            foreach (var character in piece.Text)
            {
                characters.Add(char.IsWhiteSpace(character) || character == ' ' ? (' ', piece.Format) : (character, piece.Format));
            }
        }

        var lines = new List<List<(char Character, Format Format)>> { new() };
        var breaks = 0;
        var paragraphs = new List<Paragraph>();
        void EndParagraph()
        {
            var kept = lines.Select(TrimLine).Where(line => line.Count > 0).Select(line => new Line(Runs(line))).ToList();
            if (kept.Count > 0)
            {
                paragraphs.Add(new Paragraph(kept));
            }

            lines = [new()];
        }

        foreach (var (character, format) in characters)
        {
            if (character == '\n')
            {
                breaks++;
                continue;
            }

            if (breaks > 0 && character != ' ')
            {
                if (breaks > 1)
                {
                    EndParagraph();
                }
                else
                {
                    lines.Add([]);
                }

                breaks = 0;
            }

            if (breaks == 0 || character != ' ')
            {
                lines[^1].Add((character, format));
            }
        }

        EndParagraph();
        return paragraphs;
    }

    private static List<(char Character, Format Format)> TrimLine(List<(char Character, Format Format)> line)
    {
        var collapsed = new List<(char Character, Format Format)>();
        foreach (var item in line)
        {
            if (item.Character == ' ' && (collapsed.Count == 0 || collapsed[^1].Character == ' '))
            {
                continue;
            }

            collapsed.Add(item);
        }

        if (collapsed.Count > 0 && collapsed[^1].Character == ' ')
        {
            collapsed.RemoveAt(collapsed.Count - 1);
        }

        return collapsed;
    }

    private static List<Run> Runs(List<(char Character, Format Format)> line)
    {
        // A space has the format of what stands on both sides of it when they are formatted alike, and none otherwise.
        var formats = line.Select(item => item.Format).ToArray();
        for (var i = 0; i < line.Count; i++)
        {
            if (line[i].Character == ' ')
            {
                var before = formats[i - 1];
                var after = line[i + 1].Format;
                formats[i] = before == after ? before : default;
            }
        }

        var runs = new List<Run>();
        var text = new StringBuilder();
        for (var i = 0; i < line.Count; i++)
        {
            text.Append(line[i].Character);
            if (i == line.Count - 1 || formats[i + 1] != formats[i])
            {
                runs.Add(new Run(text.ToString(), formats[i]));
                text.Clear();
            }
        }

        return runs;
    }

    // ---- Writing: Markdown ----

    private static string Write(Block block, string indent = "") => block switch
    {
        Paragraph paragraph => string.Join("\\\n" + indent, paragraph.Lines.Select(WriteLine)),
        ListBlock list => string.Join('\n', list.Items.Select((item, index) => WriteItem(list, item, index, indent))),
        _ => throw new InvalidOperationException("Unknown block."),
    };

    private static string WriteItem(ListBlock list, ListItem item, int index, string indent)
    {
        var marker = list.Ordered ? string.Create(CultureInfo.InvariantCulture, $"{index + 1}. ") : "- ";
        var inner = indent + new string(' ', marker.Length);
        var separator = item.Blocks.Count > 1 && item.Blocks.Skip(1).Any(block => block is Paragraph) ? "\n\n" + inner : "\n" + inner;
        return (index == 0 ? string.Empty : indent) + marker + string.Join(separator, item.Blocks.Select(block => Write(block, inner)));
    }

    private static string WriteLine(Line line)
    {
        var written = new StringBuilder();
        var index = 0;
        while (index < line.Runs.Count)
        {
            var href = line.Runs[index].Format.Href;
            var linked = line.Runs.Skip(index).TakeWhile(run => run.Format.Href == href).ToList();
            index += linked.Count;
            var atLineStart = written.Length == 0;
            if (href is null)
            {
                written.Append(WriteEmphasis(linked, atLineStart));
            }
            else if (linked.Count == 1 && linked[0].Format is { Bold: false, Italic: false } && linked[0].Text == href && AutolinkAddress().IsMatch(href))
            {
                written.Append('<').Append(href).Append('>');
            }
            else if (LinkAddress().IsMatch(href))
            {
                written.Append('[').Append(WriteEmphasis(linked, false, insideLink: true)).Append("](").Append(href.Contains('(', StringComparison.Ordinal) || href.Contains(')', StringComparison.Ordinal) ? $"<{href}>" : href).Append(')');
            }
            else
            {
                // An address Markdown would write otherwise (a space, a letter outside ASCII): the link as HTML.
                written.Append("<a href=\"").Append(WebUtility.HtmlEncode(href)).Append("\">").Append(WriteEmphasis(linked, false, insideLink: true)).Append("</a>");
            }
        }

        return written.ToString();
    }

    private static string WriteEmphasis(List<Run> runs, bool atLineStart, bool insideLink = false)
    {
        var written = new StringBuilder();
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            var marker = (run.Format.Bold, run.Format.Italic) switch
            {
                (true, true) => "***",
                (true, false) => "**",
                (false, true) => "*",
                _ => string.Empty,
            };
            if (marker.Length == 0)
            {
                written.Append(Escape(run.Text, atLineStart && written.Length == 0, insideLink));
                continue;
            }

            // Markdown reads its marks as emphasis only where they hug a word from outside: "*Podcast'*s" and
            // "**a*****b***" are not emphasis to it. Such a run is written with the elements themselves.
            var before = index > 0 ? runs[index - 1] : null;
            var after = index + 1 < runs.Count ? runs[index + 1] : null;
            var opens = !IsMark(run.Text[0]) || before is null || IsSpaceOrMark(before.Text[^1]);
            var closes = !IsMark(run.Text[^1]) || after is null || IsSpaceOrMark(after.Text[0]);
            var besideEmphasis = before?.Format is { Bold: true } or { Italic: true } || after?.Format is { Bold: true } or { Italic: true };
            if (opens && closes && !besideEmphasis)
            {
                written.Append(marker).Append(Escape(run.Text, false, insideLink)).Append(marker);
            }
            else
            {
                var text = Escape(run.Text, false, insideLink);
                text = run.Format.Italic ? $"<em>{text}</em>" : text;
                written.Append(run.Format.Bold ? $"<strong>{text}</strong>" : text);
            }
        }

        return written.ToString();
    }

    private static bool IsMark(char character) => char.IsPunctuation(character) || char.IsSymbol(character);

    private static bool IsSpaceOrMark(char character) => character == ' ' || IsMark(character);

    /// <summary>Text as Markdown shows it unchanged: a backslash before what Markdown, as this site reads it, would take for markup.</summary>
    internal static string Escape(string text, bool atLineStart, bool insideLink = false)
    {
        var escaped = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            var previous = i > 0 ? text[i - 1] : '\0';
            var escape = character switch
            {
                '\\' or '`' or '*' or '_' or '<' or '|' or '~' or '^' or '$' or '{' => true,
                '[' => insideLink || next == '^',
                ']' => insideLink || next is '(' or '[' or ':' or '{',
                '+' or '=' => next == character || previous == character || (atLineStart && i == 0),
                '"' => next == character || previous == character,
                '&' => Entity().IsMatch(text.AsSpan(i)),
                '#' or '>' or '-' => atLineStart && i == 0,
                // A bare address stays text, as the show wrote it: Markdown would make a link of it.
                ':' => (atLineStart && i == 0) || text.AsSpan(i + 1).StartsWith("//") || text.AsSpan(0, i).EndsWith("mailto", StringComparison.OrdinalIgnoreCase) || text.AsSpan(0, i).EndsWith("tel", StringComparison.OrdinalIgnoreCase),
                '.' when text.AsSpan(0, i).EndsWith("www", StringComparison.OrdinalIgnoreCase) => true,
                // "1. ", "J. " and "iv) " start a list item, as this site reads Markdown.
                '.' or ')' => atLineStart && i > 0 && i <= 9 && (text.AsSpan(0, i).IndexOfAnyExceptInRange('0', '9') < 0 || (i == 1 && char.IsAsciiLetter(text[0])) || text.AsSpan(0, i).IndexOfAnyExcept(RomanFigures) < 0),
                _ => false,
            };
            if (escape)
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }

    // ---- The model ----

    private readonly record struct Format(bool Bold, bool Italic, string? Href);

    private readonly record struct Piece(string Text, Format Format, bool IsBreak = false)
    {
        public static readonly Piece Break = new(string.Empty, default, true);
    }

    private sealed record Run(string Text, Format Format)
    {
        public string ToHtml()
        {
            var html = WebUtility.HtmlEncode(Text);
            html = Format.Italic ? $"<em>{html}</em>" : html;
            return Format.Bold ? $"<strong>{html}</strong>" : html;
        }
    }

    private sealed record Line(List<Run> Runs)
    {
        public bool Equals(Line? other) => other is not null && Runs.SequenceEqual(other.Runs);

        public override int GetHashCode() => Runs.Count;

        public string ToHtml()
        {
            var html = new StringBuilder();
            var index = 0;
            while (index < Runs.Count)
            {
                var href = Runs[index].Format.Href;
                var linked = Runs.Skip(index).TakeWhile(run => run.Format.Href == href).ToList();
                index += linked.Count;
                var inner = string.Concat(linked.Select(run => run.ToHtml()));
                html.Append(href is null ? inner : $"<a href=\"{WebUtility.HtmlEncode(href)}\">{inner}</a>");
            }

            return html.ToString();
        }
    }

    private abstract record Block
    {
        public abstract string Text { get; }

        /// <summary>The block as HTML on one line, which Markdown passes on as it is.</summary>
        public abstract string ToHtml();
    }

    private sealed record Paragraph(List<Line> Lines) : Block
    {
        public override string Text => string.Join(' ', Lines.Select(line => string.Concat(line.Runs.Select(run => run.Text))));

        public bool Equals(Paragraph? other) => other is not null && Lines.SequenceEqual(other.Lines);

        public override int GetHashCode() => Lines.Count;

        public override string ToHtml() => $"<p>{string.Join("<br>", Lines.Select(line => line.ToHtml()))}</p>";
    }

    private sealed record ListItem(List<Block> Blocks)
    {
        public bool Equals(ListItem? other) => other is not null && Blocks.SequenceEqual(other.Blocks);

        public override int GetHashCode() => Blocks.Count;
    }

    private sealed record ListBlock(bool Ordered, List<ListItem> Items) : Block
    {
        public override string Text => string.Join(' ', Items.SelectMany(item => item.Blocks).Select(block => block.Text));

        public bool Equals(ListBlock? other) => other is not null && Ordered == other.Ordered && Items.SequenceEqual(other.Items);

        public override int GetHashCode() => Items.Count;

        public override string ToHtml()
        {
            var name = Ordered ? "ol" : "ul";
            return $"<{name}>{string.Concat(Items.Select(item => $"<li>{string.Concat(item.Blocks.Select(block => item.Blocks.Count == 1 && block is Paragraph paragraph ? paragraph.ToHtml()[3..^4] : block.ToHtml()))}</li>"))}</{name}>";
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // &amp; &#39; &#x27; — text that would be read as a character reference.
    [GeneratedRegex(@"^&(?:[A-Za-z][A-Za-z0-9]*|#[0-9]+|#[xX][0-9A-Fa-f]+);")]
    private static partial Regex Entity();

    // What <address> may hold: an absolute address with no space and no angle bracket.
    [GeneratedRegex(@"^(?:https?://|mailto:)[A-Za-z0-9\-._:/?#@!$&'+,;=%()]+$")]
    private static partial Regex AutolinkAddress();

    // What [text](address) carries unchanged.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*:[A-Za-z0-9\-._:/?#@!$&'+,;=%()]+$")]
    private static partial Regex LinkAddress();
}
