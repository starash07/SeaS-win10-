using System.IO;
using System.Text.RegularExpressions;
using SeaS.App.Models;

namespace SeaS.App.Services;

public static class BookMetadata
{
    public const string UnknownAuthor = "未知作者";

    public static (string Title, string Author) FromFileName(string fileName)
    {
        var source = fileName.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return ("未命名书籍", UnknownAuthor);
        }

        var authorMatch = Regex.Match(source, @"^(?<title>.+?)\s*(?:作者|作\s*者)\s*[:：]\s*(?<author>.+)$");
        if (authorMatch.Success)
        {
            return (
                CleanTitle(authorMatch.Groups["title"].Value),
                CleanAuthor(authorMatch.Groups["author"].Value));
        }

        var separatorMatch = Regex.Match(source, @"^(?<title>.+?)\s*(?:-|—|–|_)+\s*(?<author>[^-_—–]+)$");
        if (separatorMatch.Success)
        {
            return (
                CleanTitle(separatorMatch.Groups["title"].Value),
                CleanAuthor(separatorMatch.Groups["author"].Value));
        }

        var byMatch = Regex.Match(source, @"^(?<title>.+?)\s+by\s+(?<author>.+)$", RegexOptions.IgnoreCase);
        if (byMatch.Success)
        {
            return (
                CleanTitle(byMatch.Groups["title"].Value),
                CleanAuthor(byMatch.Groups["author"].Value));
        }

        return (CleanTitle(source), UnknownAuthor);
    }

    public static bool Normalize(BookItem book)
    {
        var (title, author) = FromBook(book);
        var changed = false;

        if (!string.Equals(book.Title, title, StringComparison.Ordinal))
        {
            book.Title = title;
            changed = true;
        }

        if (!string.Equals(book.Author, author, StringComparison.Ordinal))
        {
            book.Author = author;
            changed = true;
        }

        return changed;
    }

    public static (string Title, string Author) FromBook(BookItem book)
    {
        var title = CleanTitle(book.Title);
        var author = CleanAuthor(book.Author);

        if (string.IsNullOrWhiteSpace(book.FilePath))
        {
            return (string.IsNullOrWhiteSpace(title) ? "未命名书籍" : title, author);
        }

        var fileName = Path.GetFileNameWithoutExtension(book.FilePath);
        var parsed = FromFileName(fileName);

        if (ShouldUseParsedTitle(title, fileName, parsed.Title, parsed.Author))
        {
            title = parsed.Title;
        }

        if (IsUnknownAuthor(author) && !IsUnknownAuthor(parsed.Author))
        {
            author = parsed.Author;
        }

        return (
            string.IsNullOrWhiteSpace(title) ? parsed.Title : title,
            string.IsNullOrWhiteSpace(author) ? UnknownAuthor : author);
    }

    private static bool ShouldUseParsedTitle(string currentTitle, string fileName, string parsedTitle, string parsedAuthor)
    {
        if (string.IsNullOrWhiteSpace(currentTitle))
        {
            return true;
        }

        if (string.Equals(currentTitle, fileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsedTitle, fileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return currentTitle.Contains("作者", StringComparison.OrdinalIgnoreCase)
               && !IsUnknownAuthor(parsedAuthor);
    }

    private static string CleanTitle(string value)
    {
        var title = value.Trim();
        title = Regex.Replace(title, @"\s+", " ");
        title = title.Trim(' ', '\t', '-', '—', '–', '_');

        if (title.Length >= 2 && title[0] == '《')
        {
            var endIndex = title.IndexOf('》');
            if (endIndex > 0)
            {
                title = title.Substring(1, endIndex - 1).Trim();
            }
        }

        return string.IsNullOrWhiteSpace(title) ? "未命名书籍" : title;
    }

    private static string CleanAuthor(string value)
    {
        var author = Regex.Replace(value.Trim(), @"\s+", " ");
        author = author.Trim(' ', '\t', '-', '—', '–', '_', ':', '：');
        return string.IsNullOrWhiteSpace(author) ? UnknownAuthor : author;
    }

    private static bool IsUnknownAuthor(string author)
    {
        return string.IsNullOrWhiteSpace(author)
               || string.Equals(author.Trim(), UnknownAuthor, StringComparison.Ordinal);
    }
}
