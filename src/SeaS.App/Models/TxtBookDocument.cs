namespace SeaS.App.Models;

public sealed class TxtBookDocument
{
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string EncodingName { get; init; } = string.Empty;
    public List<ReaderChapter> Chapters { get; init; } = [];

    public int ChapterCount => Chapters.Count;
}
