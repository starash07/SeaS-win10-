using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SeaS.App.Models;

namespace SeaS.App.Services;

public static partial class TxtBookReader
{
    private const int MaxChapterTitleLength = 48;

    public static TxtBookDocument Load(BookItem book)
    {
        var extension = Path.GetExtension(book.FilePath);
        if (string.Equals(extension, ".epub", StringComparison.OrdinalIgnoreCase))
        {
            return EpubBookReader.LoadReaderDocument(book);
        }

        if (IsMarkdownExtension(extension))
        {
            return LoadMarkdownDocument(book);
        }

        var bytes = File.ReadAllBytes(book.FilePath);
        var decoded = Decode(bytes);
        var text = NormalizeText(decoded.Text);
        var chapters = ParseChapters(text);
        var (title, author) = BookMetadata.FromBook(book);

        return new TxtBookDocument
        {
            Title = title,
            Author = author,
            FilePath = book.FilePath,
            EncodingName = decoded.EncodingName,
            Chapters = chapters.Count > 0
                ? chapters
                : [new ReaderChapter { Number = 1, Title = "正文", Paragraphs = SplitParagraphs(text) }]
        };
    }

    private static TxtBookDocument LoadMarkdownDocument(BookItem book)
    {
        var bytes = File.ReadAllBytes(book.FilePath);
        var decoded = Decode(bytes);
        var text = NormalizeText(RemoveMarkdownFrontMatter(decoded.Text));
        var chapters = ParseMarkdownChapters(text);
        var (title, author) = BookMetadata.FromBook(book);

        return new TxtBookDocument
        {
            Title = title,
            Author = author,
            FilePath = book.FilePath,
            EncodingName = $"Markdown / {decoded.EncodingName}",
            Chapters = chapters.Count > 0
                ? chapters
                : [new ReaderChapter { Number = 1, Title = "正文", Paragraphs = SplitMarkdownParagraphs(text) }]
        };
    }

    private static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return (utf8.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        foreach (var codePage in new[] { 54936, 936 })
        {
            try
            {
                var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                return (encoding.GetString(bytes), encoding.WebName.ToUpperInvariant());
            }
            catch (DecoderFallbackException)
            {
                // Try the next common Chinese TXT encoding.
            }
        }

        var fallback = Encoding.GetEncoding(936);
        return (fallback.GetString(bytes), fallback.WebName.ToUpperInvariant());
    }

    private static string NormalizeText(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\uFEFF', '\u200B', '\n', '\r', ' ', '\t');
    }

    private static List<ReaderChapter> ParseChapters(string text)
    {
        var chapters = new List<ReaderChapter>();
        var currentTitle = "正文";
        var currentLines = new List<string>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (IsChapterHeading(line))
            {
                AddChapter(chapters, currentTitle, currentLines);
                currentTitle = line;
                currentLines = [];
                continue;
            }

            currentLines.Add(rawLine);
        }

        AddChapter(chapters, currentTitle, currentLines);

        for (var i = 0; i < chapters.Count; i++)
        {
            chapters[i] = new ReaderChapter
            {
                Number = i + 1,
                Title = chapters[i].Title,
                Paragraphs = chapters[i].Paragraphs
            };
        }

        return chapters;
    }

    private static void AddChapter(List<ReaderChapter> chapters, string title, List<string> lines)
    {
        var paragraphs = SplitParagraphs(string.Join('\n', lines));
        if (paragraphs.Count == 0 && chapters.Count == 0 && title == "正文")
        {
            return;
        }

        chapters.Add(new ReaderChapter
        {
            Number = chapters.Count + 1,
            Title = title,
            Paragraphs = paragraphs
        });
    }

    private static List<string> SplitParagraphs(string text)
    {
        var paragraphs = new List<string>();

        foreach (var rawLine in text.Split('\n'))
        {
            var paragraph = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                continue;
            }

            paragraphs.Add(paragraph);
        }

        return paragraphs;
    }

    private static bool IsMarkdownExtension(string extension)
    {
        return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveMarkdownFrontMatter(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return text;
        }

        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        return end > 0 ? normalized[(end + 4)..] : text;
    }

    private static List<ReaderChapter> ParseMarkdownChapters(string text)
    {
        var chapters = new List<ReaderChapter>();
        var currentTitle = "正文";
        var currentLines = new List<string>();
        var hasMarkdownHeading = false;
        var inCodeFence = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (MarkdownFenceRegex().IsMatch(line))
            {
                inCodeFence = !inCodeFence;
                currentLines.Add(rawLine);
                continue;
            }

            if (!inCodeFence && MarkdownHeadingRegex().Match(line) is { Success: true } match)
            {
                AddMarkdownChapter(chapters, currentTitle, currentLines);
                currentTitle = CleanMarkdownInlineText(match.Groups[1].Value);
                currentLines = [];
                hasMarkdownHeading = true;
                continue;
            }

            currentLines.Add(rawLine);
        }

        AddMarkdownChapter(chapters, currentTitle, currentLines);

        if (!hasMarkdownHeading)
        {
            return [];
        }

        for (var index = 0; index < chapters.Count; index++)
        {
            chapters[index] = new ReaderChapter
            {
                Number = index + 1,
                Title = chapters[index].Title,
                Paragraphs = chapters[index].Paragraphs
            };
        }

        return chapters;
    }

    private static void AddMarkdownChapter(List<ReaderChapter> chapters, string title, List<string> lines)
    {
        var paragraphs = SplitMarkdownParagraphs(string.Join('\n', lines));
        if (paragraphs.Count == 0 && chapters.Count == 0 && title == "正文")
        {
            return;
        }

        if (paragraphs.Count == 0)
        {
            return;
        }

        chapters.Add(new ReaderChapter
        {
            Number = chapters.Count + 1,
            Title = string.IsNullOrWhiteSpace(title) ? "正文" : title,
            Paragraphs = paragraphs
        });
    }

    private static List<string> SplitMarkdownParagraphs(string text)
    {
        var paragraphs = new List<string>();
        var lines = new List<string>();
        var inCodeFence = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (MarkdownFenceRegex().IsMatch(line.Trim()))
            {
                if (inCodeFence)
                {
                    FlushMarkdownParagraph(paragraphs, lines, preserveLineBreaks: true);
                }
                else
                {
                    FlushMarkdownParagraph(paragraphs, lines, preserveLineBreaks: false);
                }

                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence)
            {
                lines.Add(rawLine.TrimEnd());
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)
                || MarkdownHorizontalRuleRegex().IsMatch(line.Trim())
                || MarkdownHeadingRegex().IsMatch(line.Trim()))
            {
                FlushMarkdownParagraph(paragraphs, lines, preserveLineBreaks: false);
                continue;
            }

            lines.Add(line);
        }

        FlushMarkdownParagraph(paragraphs, lines, preserveLineBreaks: inCodeFence);
        return paragraphs;
    }

    private static void FlushMarkdownParagraph(ICollection<string> paragraphs, List<string> lines, bool preserveLineBreaks)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var raw = preserveLineBreaks ? string.Join('\n', lines) : string.Join(' ', lines);
        lines.Clear();
        var plain = CleanMarkdownParagraph(raw);
        if (!string.IsNullOrWhiteSpace(plain))
        {
            paragraphs.Add(plain);
        }
    }

    private static string CleanMarkdownParagraph(string text)
    {
        var value = text.Trim();
        value = MarkdownImageRegex().Replace(value, match =>
            string.IsNullOrWhiteSpace(match.Groups[1].Value) ? string.Empty : match.Groups[1].Value);
        value = MarkdownLinkRegex().Replace(value, "$1");
        value = MarkdownHtmlTagRegex().Replace(value, string.Empty);
        value = MarkdownBlockQuotePrefixRegex().Replace(value, "$1");
        value = MarkdownListPrefixRegex().Replace(value, "$1");
        value = MarkdownInlineCodeRegex().Replace(value, "$1");
        return CleanMarkdownInlineText(value);
    }

    private static string CleanMarkdownInlineText(string text)
    {
        return text
            .Replace("**", string.Empty)
            .Replace("__", string.Empty)
            .Replace("~~", string.Empty)
            .Replace("*", string.Empty)
            .Replace("`", string.Empty)
            .Replace("\\[", "[")
            .Replace("\\]", "]")
            .Trim();
    }

    private static bool IsChapterHeading(string line)
    {
        return line.Length is > 0 and <= MaxChapterTitleLength
               && ChapterHeadingRegex().IsMatch(line);
    }

    [GeneratedRegex(@"^(?:第\s*[0-9０-９零〇一二三四五六七八九十百千万两壹贰叁肆伍陆柒捌玖拾佰仟]+\s*[章节卷回部集].*|Chapter\s+\d+.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterHeadingRegex();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex MarkdownHeadingRegex();

    [GeneratedRegex(@"^\s{0,3}```")]
    private static partial Regex MarkdownFenceRegex();

    [GeneratedRegex(@"^\s{0,3}(?:-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex MarkdownHorizontalRuleRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]+\)")]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex MarkdownHtmlTagRegex();

    [GeneratedRegex(@"(^|\n)\s{0,3}>\s?")]
    private static partial Regex MarkdownBlockQuotePrefixRegex();

    [GeneratedRegex(@"(^|\n)\s{0,3}(?:[-*+]|\d+[.)])\s+")]
    private static partial Regex MarkdownListPrefixRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex MarkdownInlineCodeRegex();
}
