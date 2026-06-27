namespace SeaS.App.Models;

public sealed class WindowPreferences
{
    public bool HasPlacement { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1120;
    public double Height { get; set; } = 720;
    public bool IsMaximized { get; set; }
}
