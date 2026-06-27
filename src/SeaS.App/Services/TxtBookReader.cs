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

    private static bool IsChapterHeading(string line)
    {
        return line.Length is > 0 and <= MaxChapterTitleLength
               && ChapterHeadingRegex().IsMatch(line);
    }

    [GeneratedRegex(@"^(?:第\s*[0-9０-９零〇一二三四五六七八九十百千万两壹贰叁肆伍陆柒捌玖拾佰仟]+\s*[章节卷回部集].*|Chapter\s+\d+.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterHeadingRegex();
}
