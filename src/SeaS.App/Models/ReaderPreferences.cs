namespace SeaS.App.Models;

public sealed class ReaderPreferences
{
    public double FontSize { get; set; } = 18;
    public double LineHeight { get; set; } = 1.95;
    public double ContentWidthPercent { get; set; } = 95;
    public double? PageMarginPixels { get; set; }
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public string ReadingMode { get; set; } = "scroll";
    public string Theme { get; set; } = "white";
    public string CustomBackgroundColor { get; set; } = "#F7FAFC";
    public string CustomTextColor { get; set; } = "#3F4B52";
    public bool ImmersiveMode { get; set; }
    public bool AutoPageEnabled { get; set; }
    public double AutoPageIntervalSeconds { get; set; } = 5;
}
