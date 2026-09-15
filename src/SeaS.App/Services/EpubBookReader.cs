using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SeaS.App.Models;

namespace SeaS.App.Services;

public sealed class EpubBookDocument : IDisposable
{
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public byte[]? CoverImageBytes { get; init; }
    public IReadOnlyList<EpubChapter> Chapters { get; init; } = [];
    public IReadOnlyList<EpubNavigationEntry> NavigationEntries { get; init; } = [];
    internal ZipArchive Archive { get; init; } = null!;
    internal string PackageDirectory { get; init; } = string.Empty;
    internal IReadOnlyDictionary<string, EpubManifestItem> Manifest { get; init; } =
        new Dictionary<string, EpubManifestItem>(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        Archive.Dispose();
    }

    public byte[]? ReadResourceBytes(string chapterPath, string href)
    {
        var resourcePath = EpubPath.Resolve(EpubPath.GetDirectory(chapterPath), href);
        return ReadEntryBytes(resourcePath);
    }

    internal byte[]? ReadEntryBytes(string path)
    {
        var normalizedPath = EpubPath.Normalize(path);
        return Archive.GetEntry(normalizedPath) is { } entry ? ReadEntryBytes(entry) : null;
    }

    internal static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

public sealed class EpubChapter
{
    public int Index { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

public sealed class EpubNavigationEntry
{
    public string Title { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
}

internal sealed class EpubManifestItem
{
    public string Id { get; init; } = string.Empty;
    public string Href { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string MediaType { get; init; } = string.Empty;
    public string Properties { get; init; } = string.Empty;
}

public sealed record EpubMetadata(string Title, string Author, byte[]? CoverImageBytes);

public static partial class EpubBookReader
{
    private static readonly XNamespace ContainerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace OpfNamespace = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";

    public static EpubMetadata ReadMetadata(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var packagePath = GetPackagePath(archive);
        var packageDirectory = EpubPath.GetDirectory(packagePath);
        var packageDocument = ReadXml(archive, packagePath);
        var manifest = ReadManifest(packageDocument, packageDirectory);

        var metadata = packageDocument.Root?.Element(OpfNamespace + "metadata");
        var title = metadata?.Element(DcNamespace + "title")?.Value.Trim();
        var author = metadata?.Element(DcNamespace + "creator")?.Value.Trim();

        return new EpubMetadata(
            string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(filePath) : title,
            string.IsNullOrWhiteSpace(author) ? "未知作者" : author,
            ReadCoverImageBytes(archive, packageDocument, manifest));
    }

    public static EpubBookDocument Load(string filePath)
    {
        var archive = ZipFile.OpenRead(filePath);
        try
        {
            var packagePath = GetPackagePath(archive);
            var packageDirectory = EpubPath.GetDirectory(packagePath);
            var packageDocument = ReadXml(archive, packagePath);
            var manifest = ReadManifest(packageDocument, packageDirectory);
            var spineIds = packageDocument.Root?
                .Element(OpfNamespace + "spine")?
                .Elements(OpfNamespace + "itemref")
                .Select(element => element.Attribute("idref")?.Value)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToList() ?? [];

            var navigationEntries = ReadNavigationEntries(archive, packageDocument, manifest);
            var navigationTitles = navigationEntries
                .GroupBy(entry => EpubPath.StripFragment(entry.TargetPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.OrdinalIgnoreCase);
            var chapters = new List<EpubChapter>();
            foreach (var id in spineIds)
            {
                if (!manifest.TryGetValue(id, out var item) || !IsHtmlMediaType(item.MediaType))
                {
                    continue;
                }

                var content = ReadTextEntry(archive, item.FullPath);
                chapters.Add(new EpubChapter
                {
                    Index = chapters.Count,
                    Path = item.FullPath,
                    Content = content,
                    Title = ResolveChapterTitle(content, item, navigationTitles)
                });
            }

            var metadata = packageDocument.Root?.Element(OpfNamespace + "metadata");
            var title = metadata?.Element(DcNamespace + "title")?.Value.Trim();
            var author = metadata?.Element(DcNamespace + "creator")?.Value.Trim();

            return new EpubBookDocument
            {
                Archive = archive,
                PackageDirectory = packageDirectory,
                Manifest = manifest,
                Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(filePath) : title,
                Author = string.IsNullOrWhiteSpace(author) ? "未知作者" : author,
                CoverImageBytes = ReadCoverImageBytes(archive, packageDocument, manifest),
                Chapters = chapters,
                NavigationEntries = navigationEntries
            };
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public static TxtBookDocument LoadReaderDocument(BookItem book)
    {
        using var epub = Load(book.FilePath);
        var chapters = new List<ReaderChapter>();
        var chapterPathIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var epubChapter in epub.Chapters)
        {
            var blocks = ExtractReaderBlocks(epub, epubChapter);
            var paragraphs = blocks
                .Where(block => block.Kind == ReaderContentBlockKind.Text && !string.IsNullOrWhiteSpace(block.Text))
                .Select(block => block.Text)
                .ToList();
            if (blocks.Count == 0 && paragraphs.Count == 0)
            {
                continue;
            }

            var chapterIndex = chapters.Count;
            chapters.Add(new ReaderChapter
            {
                Number = chapterIndex + 1,
                Title = string.IsNullOrWhiteSpace(epubChapter.Title) ? $"第 {chapterIndex + 1:00} 章" : epubChapter.Title,
                Blocks = blocks,
                Paragraphs = paragraphs,
                AnchorParagraphIndices = BuildAnchorParagraphIndices(blocks)
            });
            chapterPathIndices[EpubPath.StripFragment(epubChapter.Path)] = chapterIndex;
        }

        if (chapters.Count == 0)
        {
            chapters.Add(new ReaderChapter
            {
                Number = 1,
                Title = "正文",
                Paragraphs = ["这个 EPUB 暂时没有可显示的内容。"],
                Blocks =
                [
                    new ReaderContentBlock
                    {
                        Kind = ReaderContentBlockKind.Text,
                        Text = "这个 EPUB 暂时没有可显示的内容。"
                    }
                ]
            });
        }

        var navigationItems = new List<ReaderNavigationItem>();
        foreach (var entry in epub.NavigationEntries)
        {
            var targetPath = EpubPath.StripFragment(entry.TargetPath);
            if (!chapterPathIndices.TryGetValue(targetPath, out var chapterIndex))
            {
                continue;
            }

            var fragment = EpubPath.GetFragment(entry.TargetPath);
            var paragraphIndex = !string.IsNullOrWhiteSpace(fragment)
                                 && chapters[chapterIndex].AnchorParagraphIndices.TryGetValue(fragment, out var anchorIndex)
                ? anchorIndex
                : -1;
            navigationItems.Add(new ReaderNavigationItem
            {
                Number = navigationItems.Count + 1,
                Title = string.IsNullOrWhiteSpace(entry.Title) ? chapters[chapterIndex].Title : entry.Title,
                ChapterIndex = chapterIndex,
                ParagraphIndex = paragraphIndex
            });
        }

        return new TxtBookDocument
        {
            Title = string.IsNullOrWhiteSpace(epub.Title) ? book.Title : epub.Title,
            Author = string.IsNullOrWhiteSpace(epub.Author) ? book.Author : epub.Author,
            FilePath = book.FilePath,
            EncodingName = "EPUB",
            Chapters = chapters,
            NavigationItems = navigationItems
        };
    }

    private static Dictionary<string, int> BuildAnchorParagraphIndices(IEnumerable<ReaderContentBlock> blocks)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var paragraphIndex = 0;
        foreach (var block in blocks)
        {
            var targetParagraphIndex = block.Kind == ReaderContentBlockKind.Text
                ? paragraphIndex
                : Math.Max(0, paragraphIndex - 1);
            foreach (var anchorId in block.AnchorIds)
            {
                result.TryAdd(anchorId, targetParagraphIndex);
            }

            if (block.Kind == ReaderContentBlockKind.Text && !string.IsNullOrWhiteSpace(block.Text))
            {
                paragraphIndex++;
            }
        }

        return result;
    }

    private static List<ReaderContentBlock> ExtractReaderBlocks(EpubBookDocument document, EpubChapter chapter)
    {
        var blocks = new List<ReaderContentBlock>();
        try
        {
            var parsed = XDocument.Parse(chapter.Content, LoadOptions.PreserveWhitespace);
            var body = parsed.Descendants().FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "body", StringComparison.OrdinalIgnoreCase));
            if (body is not null)
            {
                foreach (var child in body.Elements())
                {
                    AddReaderBlocks(document, chapter, child, blocks);
                }
            }
        }
        catch
        {
            var text = NormalizeText(StripHtml(chapter.Content));
            if (!string.IsNullOrWhiteSpace(text))
            {
                blocks.Add(new ReaderContentBlock
                {
                    Kind = ReaderContentBlockKind.Text,
                    Text = text
                });
            }
        }

        return blocks;
    }

    private static void AddReaderBlocks(
        EpubBookDocument document,
        EpubChapter chapter,
        XElement element,
        List<ReaderContentBlock> blocks)
    {
        var firstAddedBlockIndex = blocks.Count;
        var name = element.Name.LocalName.ToLowerInvariant();
        if (name is "script" or "style" or "head" or "title" or "meta" or "link")
        {
            return;
        }

        if (IsImageElement(element))
        {
            AddImageBlock(document, chapter, element, blocks);
            AttachElementAnchors(element, blocks, firstAddedBlockIndex);
            return;
        }

        if (name is "p" or "li" or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
        {
            AddMixedInlineBlocks(document, chapter, element, blocks);
            AttachElementAnchors(element, blocks, firstAddedBlockIndex);
            return;
        }

        if (name is "br")
        {
            return;
        }

        if (!element.HasElements)
        {
            AddTextBlock(blocks, element.Value);
            AttachElementAnchors(element, blocks, firstAddedBlockIndex);
            return;
        }

        foreach (var child in element.Elements())
        {
            AddReaderBlocks(document, chapter, child, blocks);
        }

        AttachElementAnchors(element, blocks, firstAddedBlockIndex);
    }

    private static void AddMixedInlineBlocks(
        EpubBookDocument document,
        EpubChapter chapter,
        XElement element,
        List<ReaderContentBlock> blocks)
    {
        var text = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText textNode:
                    text.Append(textNode.Value);
                    break;
                case XElement child when IsImageElement(child):
                    FlushTextBlock(blocks, text);
                    AddImageBlock(document, chapter, child, blocks);
                    break;
                case XElement child when child.Name.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase):
                    text.Append('\n');
                    break;
                case XElement child when IsBlockElement(child):
                    FlushTextBlock(blocks, text);
                    AddReaderBlocks(document, chapter, child, blocks);
                    break;
                case XElement child:
                    AppendInlineText(text, child);
                    foreach (var image in child.Descendants().Where(IsImageElement))
                    {
                        FlushTextBlock(blocks, text);
                        AddImageBlock(document, chapter, image, blocks);
                    }
                    break;
            }
        }

        FlushTextBlock(blocks, text);
    }

    private static void AppendInlineText(StringBuilder text, XElement element)
    {
        if (element.Name.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            text.Append('\n');
            return;
        }

        if (!IsImageElement(element))
        {
            text.Append(' ');
            text.Append(element.Value);
            text.Append(' ');
        }
    }

    private static void FlushTextBlock(ICollection<ReaderContentBlock> blocks, StringBuilder text)
    {
        var normalized = NormalizeText(text.ToString());
        text.Clear();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            AddTextBlock(blocks, normalized);
        }
    }

    private static void AddTextBlock(ICollection<ReaderContentBlock> blocks, string text)
    {
        var normalized = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        blocks.Add(new ReaderContentBlock
        {
            Kind = ReaderContentBlockKind.Text,
            Text = normalized
        });
    }

    private static void AddImageBlock(
        EpubBookDocument document,
        EpubChapter chapter,
        XElement element,
        ICollection<ReaderContentBlock> blocks)
    {
        var href = GetImageHref(element);
        if (string.IsNullOrWhiteSpace(href)
            || document.ReadResourceBytes(chapter.Path, href) is not { Length: > 0 } bytes)
        {
            return;
        }

        blocks.Add(new ReaderContentBlock
        {
            Kind = ReaderContentBlockKind.Image,
            ImageBytes = bytes,
            ImageAlt = element.Attribute("alt")?.Value ?? "图片"
        });
    }

    private static void AttachElementAnchors(
        XElement element,
        IReadOnlyList<ReaderContentBlock> blocks,
        int firstAddedBlockIndex)
    {
        if (firstAddedBlockIndex >= blocks.Count)
        {
            return;
        }

        var anchorIds = element.Attributes()
            .Where(attribute => attribute.Name.LocalName is "id" or "name")
            .Select(attribute => Uri.UnescapeDataString(attribute.Value.Trim()))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var anchorId in anchorIds)
        {
            if (!blocks[firstAddedBlockIndex].AnchorIds.Contains(anchorId, StringComparer.OrdinalIgnoreCase))
            {
                blocks[firstAddedBlockIndex].AnchorIds.Add(anchorId);
            }
        }
    }

    private static bool IsBlockElement(XElement element)
    {
        return element.Name.LocalName.ToLowerInvariant() is
            "p" or "div" or "section" or "article" or "main" or "figure" or "blockquote"
            or "ul" or "ol" or "li"
            or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
    }

    private static bool IsImageElement(XElement element)
    {
        var name = element.Name.LocalName;
        return string.Equals(name, "img", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "image", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "svg", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetImageHref(XElement element)
    {
        if (string.Equals(element.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            element = element.Descendants().FirstOrDefault(IsImageElement) ?? element;
        }

        return element.Attribute("src")?.Value
            ?? element.Attribute("href")?.Value
            ?? element.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"))?.Value;
    }

    private static string StripHtml(string html)
    {
        return TagRegex().Replace(html, " ");
    }

    private static string GetPackagePath(ZipArchive archive)
    {
        var container = ReadXml(archive, "META-INF/container.xml");
        var packagePath = container
            .Root?
            .Element(ContainerNamespace + "rootfiles")?
            .Elements(ContainerNamespace + "rootfile")
            .FirstOrDefault()?
            .Attribute("full-path")?
            .Value;

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new InvalidDataException("这个 EPUB 缺少 package 文档。");
        }

        return EpubPath.Normalize(packagePath);
    }

    private static Dictionary<string, EpubManifestItem> ReadManifest(XDocument packageDocument, string packageDirectory)
    {
        return packageDocument.Root?
            .Element(OpfNamespace + "manifest")?
            .Elements(OpfNamespace + "item")
            .Select(element =>
            {
                var id = element.Attribute("id")?.Value ?? string.Empty;
                var href = element.Attribute("href")?.Value ?? string.Empty;
                return new EpubManifestItem
                {
                    Id = id,
                    Href = href,
                    FullPath = EpubPath.Resolve(packageDirectory, href),
                    MediaType = element.Attribute("media-type")?.Value ?? string.Empty,
                    Properties = element.Attribute("properties")?.Value ?? string.Empty
                };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, EpubManifestItem>(StringComparer.OrdinalIgnoreCase);
    }

    private static byte[]? ReadCoverImageBytes(
        ZipArchive archive,
        XDocument packageDocument,
        IReadOnlyDictionary<string, EpubManifestItem> manifest)
    {
        var coverId = packageDocument.Root?
            .Element(OpfNamespace + "metadata")?
            .Elements(OpfNamespace + "meta")
            .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("content")
            ?.Value;

        if (!string.IsNullOrWhiteSpace(coverId) && manifest.TryGetValue(coverId, out var coverItem))
        {
            return archive.GetEntry(coverItem.FullPath) is { } coverEntry ? EpubBookDocument.ReadEntryBytes(coverEntry) : null;
        }

        var propertyCover = manifest.Values.FirstOrDefault(item =>
            item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(property => string.Equals(property, "cover-image", StringComparison.OrdinalIgnoreCase)));
        if (propertyCover is not null)
        {
            return archive.GetEntry(propertyCover.FullPath) is { } coverEntry ? EpubBookDocument.ReadEntryBytes(coverEntry) : null;
        }

        var fallbackCover = manifest.Values.FirstOrDefault(item =>
            item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && (item.Href.Contains("cover", StringComparison.OrdinalIgnoreCase)
                || item.Id.Contains("cover", StringComparison.OrdinalIgnoreCase)));

        return fallbackCover is not null && archive.GetEntry(fallbackCover.FullPath) is { } entry
            ? EpubBookDocument.ReadEntryBytes(entry)
            : null;
    }

    private static List<EpubNavigationEntry> ReadNavigationEntries(
        ZipArchive archive,
        XDocument packageDocument,
        IReadOnlyDictionary<string, EpubManifestItem> manifest)
    {
        var result = new List<EpubNavigationEntry>();
        var navItem = manifest.Values.FirstOrDefault(item =>
            item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(property => string.Equals(property, "nav", StringComparison.OrdinalIgnoreCase)));
        if (navItem is not null && archive.GetEntry(navItem.FullPath) is { } entry)
        {
            try
            {
                var navDocument = XDocument.Parse(ReadEntryText(entry), LoadOptions.PreserveWhitespace);
                var navigationRoot = navDocument.Descendants()
                    .Where(element => string.Equals(element.Name.LocalName, "nav", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(element => element.Attributes().Any(attribute =>
                        attribute.Name.LocalName == "type"
                        && attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Any(value => string.Equals(value, "toc", StringComparison.OrdinalIgnoreCase))))
                    ?? navDocument.Descendants().FirstOrDefault(element =>
                        string.Equals(element.Name.LocalName, "nav", StringComparison.OrdinalIgnoreCase));
                if (navigationRoot is not null)
                {
                    AddNavigationAnchors(result, navigationRoot, navItem.FullPath);
                }
            }
            catch
            {
            }
        }

        if (result.Count == 0)
        {
            var tocId = packageDocument.Root?
                .Element(OpfNamespace + "spine")?
                .Attribute("toc")?
                .Value;
            var ncxItem = !string.IsNullOrWhiteSpace(tocId) && manifest.TryGetValue(tocId, out var declaredNcx)
                ? declaredNcx
                : manifest.Values.FirstOrDefault(item =>
                    item.MediaType.Equals("application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase)
                    || item.Href.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase));
            if (ncxItem is not null && archive.GetEntry(ncxItem.FullPath) is { } ncxEntry)
            {
                try
                {
                    var ncxDocument = XDocument.Parse(ReadEntryText(ncxEntry), LoadOptions.PreserveWhitespace);
                    foreach (var navPoint in ncxDocument.Descendants().Where(element =>
                                 string.Equals(element.Name.LocalName, "navPoint", StringComparison.OrdinalIgnoreCase)))
                    {
                        var title = NormalizeText(navPoint.Descendants().FirstOrDefault(element =>
                            string.Equals(element.Name.LocalName, "text", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty);
                        var source = navPoint.Elements().FirstOrDefault(element =>
                            string.Equals(element.Name.LocalName, "content", StringComparison.OrdinalIgnoreCase))?.Attribute("src")?.Value;
                        AddNavigationEntry(result, title, source, ncxItem.FullPath);
                    }
                }
                catch
                {
                }
            }
        }

        return result
            .GroupBy(entry => $"{entry.TargetPath}\u001f{entry.Title}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void AddNavigationAnchors(
        ICollection<EpubNavigationEntry> result,
        XElement navigationRoot,
        string navigationPath)
    {
        foreach (var anchor in navigationRoot.Descendants().Where(element =>
                     string.Equals(element.Name.LocalName, "a", StringComparison.OrdinalIgnoreCase)))
        {
            AddNavigationEntry(
                result,
                NormalizeText(anchor.Value),
                anchor.Attribute("href")?.Value,
                navigationPath);
        }
    }

    private static void AddNavigationEntry(
        ICollection<EpubNavigationEntry> result,
        string title,
        string? href,
        string navigationPath)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href))
        {
            return;
        }

        result.Add(new EpubNavigationEntry
        {
            Title = title,
            TargetPath = EpubPath.ResolveReference(EpubPath.GetDirectory(navigationPath), href)
        });
    }

    private static string ResolveChapterTitle(
        string content,
        EpubManifestItem item,
        IReadOnlyDictionary<string, string> navigationTitles)
    {
        if (navigationTitles.TryGetValue(EpubPath.StripFragment(item.FullPath), out var navigationTitle)
            && !string.IsNullOrWhiteSpace(navigationTitle))
        {
            return navigationTitle;
        }

        try
        {
            var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
            var heading = document
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName is "h1" or "h2" or "h3"
                    && !string.IsNullOrWhiteSpace(element.Value));
            if (heading is not null)
            {
                return NormalizeText(heading.Value);
            }

            var title = document
                .Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (!string.IsNullOrWhiteSpace(title))
            {
                return NormalizeText(title);
            }
        }
        catch
        {
        }

        return Path.GetFileNameWithoutExtension(item.Href);
    }

    private static bool IsHtmlMediaType(string mediaType)
    {
        return mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        if (archive.GetEntry(EpubPath.Normalize(path)) is not { } entry)
        {
            throw new FileNotFoundException($"EPUB 中缺少文件：{path}");
        }

        return XDocument.Parse(ReadEntryText(entry), LoadOptions.PreserveWhitespace);
    }

    private static string ReadTextEntry(ZipArchive archive, string path)
    {
        if (archive.GetEntry(EpubPath.Normalize(path)) is not { } entry)
        {
            throw new FileNotFoundException($"EPUB 中缺少文件：{path}");
        }

        return ReadEntryText(entry);
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string NormalizeText(string text)
    {
        return WebUtility.HtmlDecode(WhitespaceRegex().Replace(text, " ")).Trim();
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

internal static class EpubPath
{
    public static string Normalize(string path)
    {
        return Uri.UnescapeDataString(path)
            .Replace('\\', '/')
            .TrimStart('/');
    }

    public static string StripFragment(string path)
    {
        var normalized = Normalize(path);
        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            normalized = normalized[..fragmentIndex];
        }

        var queryIndex = normalized.IndexOf('?');
        return queryIndex >= 0 ? normalized[..queryIndex] : normalized;
    }

    public static string GetFragment(string path)
    {
        var normalized = Normalize(path);
        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex < 0 || fragmentIndex == normalized.Length - 1)
        {
            return string.Empty;
        }

        var fragment = normalized[(fragmentIndex + 1)..];
        var queryIndex = fragment.IndexOf('?');
        return Uri.UnescapeDataString(queryIndex >= 0 ? fragment[..queryIndex] : fragment);
    }

    public static string GetDirectory(string path)
    {
        var normalized = StripFragment(path);
        var index = normalized.LastIndexOf('/');
        return index < 0 ? string.Empty : normalized[..index];
    }

    public static string Resolve(string baseDirectory, string href)
    {
        href = StripFragment(href);
        if (string.IsNullOrWhiteSpace(href))
        {
            return Normalize(baseDirectory);
        }

        if (href.StartsWith('/'))
        {
            return Normalize(href);
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            parts.AddRange(Normalize(baseDirectory).Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var part in Normalize(href).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    public static string ResolveReference(string baseDirectory, string href)
    {
        var fragment = GetFragment(href);
        var resolvedPath = Resolve(baseDirectory, href);
        return string.IsNullOrWhiteSpace(fragment) ? resolvedPath : $"{resolvedPath}#{fragment}";
    }
}
