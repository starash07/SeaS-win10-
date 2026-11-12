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
    public string? ImageWidth { get; init; }
    public string? ImageHeight { get; init; }
    public string? ImageMaxWidth { get; init; }
    public string? ImageMaxHeight { get; init; }
    public string? ImageDisplay { get; init; }
    public string? ImageVerticalAlignment { get; init; }
    public string? ImageHorizontalAlignment { get; init; }
    public List<ReaderInlineImage> InlineImages { get; init; } = [];
    public List<string> AnchorIds { get; init; } = [];
}

public sealed record ReaderInlineImage(int CharacterIndex, ReaderContentBlock Image);
