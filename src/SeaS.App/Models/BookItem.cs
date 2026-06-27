using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SeaS.App.Models;

public sealed class BookItem : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _author = "未知作者";
    private bool _isFavorite;
    private bool _isMissing;
    private bool _isBatchSelected;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Author
    {
        get => _author;
        set => SetField(ref _author, value);
    }

    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? LastOpenedAt { get; set; }
    public int OpenCount { get; set; }
    public int LastReadChapterIndex { get; set; }
    public double LastReadChapterProgress { get; set; }
    public Guid? GroupId { get; set; }
    public DateTime? FavoritedAt { get; set; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetField(ref _isFavorite, value);
    }

    [JsonIgnore]
    public bool IsMissing
    {
        get => _isMissing;
        set => SetField(ref _isMissing, value);
    }

    [JsonIgnore]
    public bool IsBatchSelected
    {
        get => _isBatchSelected;
        set => SetField(ref _isBatchSelected, value);
    }

    [JsonIgnore]
    public string FileSizeText => FormatFileSize(FileSize);

    [JsonIgnore]
    public string Extension => Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshFileState()
    {
        IsMissing = !File.Exists(FilePath);
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private void SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
