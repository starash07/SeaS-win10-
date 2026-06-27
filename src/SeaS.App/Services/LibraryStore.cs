using System.Text.Json;
using System.IO;
using SeaS.App.Models;

namespace SeaS.App.Services;

public sealed class LibraryState
{
    public string Mode { get; set; } = "leisure";
    public string AllBooksSortMode { get; set; } = "recent";
    public bool AllBooksSortDescending { get; set; } = true;
    public List<BookItem> Books { get; set; } = [];
    public List<BookGroup> Groups { get; set; } = [];
    public List<ReaderBookmark> Bookmarks { get; set; } = [];
    public ReaderPreferences ReaderPreferences { get; set; } = new();
    public WindowPreferences WindowPreferences { get; set; } = new();
}

public static class LibraryStore
{
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SeaS");

    private static readonly string StatePath = Path.Combine(DataDirectory, "library.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static LibraryState Load()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new LibraryState();
            }

            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<LibraryState>(json, JsonOptions) ?? new LibraryState();
        }
        catch
        {
            return new LibraryState();
        }
    }

    public static void Save(LibraryState state)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = StatePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, StatePath, true);
    }
}
