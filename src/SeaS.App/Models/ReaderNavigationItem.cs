namespace SeaS.App.Models;

public sealed class ReaderNavigationItem
{
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public int ChapterIndex { get; init; }
    public int ParagraphIndex { get; init; } = -1;
}
