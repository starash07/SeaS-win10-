namespace SeaS.App.Models;

public sealed class ReaderBookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public int ChapterIndex { get; set; }
    public double ChapterProgress { get; set; }
    public string Title { get; set; } = "当前位置";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
