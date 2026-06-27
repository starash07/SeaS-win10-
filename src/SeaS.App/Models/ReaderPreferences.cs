namespace SeaS.App.Models;

public sealed class ReaderPreferences
{
    public double FontSize { get; set; } = 18;
    public double LineHeight { get; set; } = 1.95;
    public double ContentWidthPercent { get; set; } = 95;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public string ReadingMode { get; set; } = "scroll";
    public string Theme { get; set; } = "white";
    public bool ImmersiveMode { get; set; }
    public bool AutoPageEnabled { get; set; }
    public double AutoPageIntervalSeconds { get; set; } = 5;
}
