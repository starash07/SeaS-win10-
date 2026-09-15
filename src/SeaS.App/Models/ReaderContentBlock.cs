namespace SeaS.App.Models;

public enum ReaderContentBlockKind
{
    Text,
    Image
}

public sealed class ReaderContentBlock
{
    public ReaderContentBlockKind Kind { get; init; } = ReaderContentBlockKind.Text;
    public string Text { get; init; } = string.Empty;
    public byte[]? ImageBytes { get; init; }
    public string ImageAlt { get; init; } = string.Empty;
    public List<string> AnchorIds { get; init; } = [];
}
