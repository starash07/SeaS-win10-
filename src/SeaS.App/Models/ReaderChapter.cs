namespace SeaS.App.Models;

public sealed class ReaderChapter
{
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public List<string> Paragraphs { get; init; } = [];
}
