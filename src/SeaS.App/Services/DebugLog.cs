using System.Diagnostics;
using System.IO;

namespace SeaS.App.Services;

internal static class DebugLog
{
    [Conditional("DEBUG")]
    public static void WriteException(string source, Exception exception)
    {
#if DEBUG
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeaS",
                "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "debug.log");
            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Debug logging must never replace the original failure.
        }
#endif
    }
}
