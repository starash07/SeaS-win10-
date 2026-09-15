using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace SeaS.App.Models;

public sealed class BookItem : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _author = "未知作者";
    private string _coverImagePath = string.Empty;
    private BitmapSource? _coverImage;
    private string? _coverImageCachePath;
    private DateTime? _lastOpenedAt;
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
    public string CoverImagePath
    {
        get => _coverImagePath;
        set
        {
            if (SetField(ref _coverImagePath, value))
            {
                RefreshCoverImage();
            }
        }
    }

    public long FileSize { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? LastOpenedAt
    {
        get => _lastOpenedAt;
        set
        {
            if (SetField(ref _lastOpenedAt, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastOpenedAtText)));
            }
        }
    }

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

    [JsonIgnore]
    public string LastOpenedAtText => LastOpenedAt.HasValue
        ? $"上次打开 {LastOpenedAt.Value:yyyy-MM-dd}"
        : "未打开";

    [JsonIgnore]
    public bool HasCover
    {
        get => !string.IsNullOrWhiteSpace(CoverImagePath) && File.Exists(CoverImagePath);
    }

    [JsonIgnore]
    public BitmapSource? CoverImage
    {
        get
        {
            EnsureCoverImageLoaded();
            return _coverImage;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshFileState()
    {
        IsMissing = !File.Exists(FilePath);
    }

    public void RefreshCoverImage()
    {
        _coverImageCachePath = null;
        EnsureCoverImageLoaded();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
    }

    private void EnsureCoverImageLoaded()
    {
        if (string.Equals(_coverImageCachePath, CoverImagePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _coverImage = LoadCoverImage(CoverImagePath);
        _coverImageCachePath = CoverImagePath;
    }

    private static BitmapSource? LoadCoverImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
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

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
