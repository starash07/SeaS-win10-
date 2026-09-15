using SeaS.App.Models;
using SeaS.App.Services;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SeaS.App;

public partial class BookDetailsWindow : Window
{
    private readonly BookItem _book;
    private readonly string _originalCoverImagePath;
    private string _coverImagePath;
    private string? _temporaryCoverImagePath;

    public BookDetailsWindow(BookItem book)
    {
        InitializeComponent();
        DialogMotion.EnablePop(DialogRoot);
        _book = book;
        _originalCoverImagePath = book.CoverImagePath;
        _coverImagePath = book.CoverImagePath;
        TitleBox.Text = book.Title;
        AuthorBox.Text = book.Author;
        RefreshCoverPreview();
        TitleBox.SelectAll();
        TitleBox.Focus();
    }

    private void ChooseCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择封面图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.webp|所有文件|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var outputPath = Path.Combine(CoverEditTempDirectory, $"{_book.Id:N}-{Guid.NewGuid():N}.png");
        try
        {
            var cropWindow = new ImageCropWindow(dialog.FileName, outputPath)
            {
                Owner = this
            };

            if (cropWindow.ShowDialog() == true)
            {
                DeleteTemporaryCover();
                _temporaryCoverImagePath = outputPath;
                _coverImagePath = outputPath;
                RefreshCoverPreview();
            }
        }
        catch (Exception exception)
        {
            TryDeleteFile(outputPath);
            SeaSMessageBox.Show(this, $"无法读取这张图片：{exception.Message}", "SeaS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteCover_Click(object sender, RoutedEventArgs e)
    {
        DeleteTemporaryCover();
        _coverImagePath = string.Empty;
        RefreshCoverPreview();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        var author = AuthorBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            SeaSMessageBox.Show(this, "书名不能为空。", "SeaS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string finalCoverImagePath;
        try
        {
            finalCoverImagePath = CommitCoverChanges();
        }
        catch (Exception exception)
        {
            SeaSMessageBox.Show(this, $"保存封面失败：{exception.Message}", "SeaS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _book.Title = title;
        _book.Author = string.IsNullOrWhiteSpace(author) ? "未知作者" : author;
        _book.CoverImagePath = finalCoverImagePath;
        _book.RefreshCoverImage();
        DialogResult = true;
    }

    private void RefreshCoverPreview()
    {
        DeleteCoverButton.IsEnabled = !string.IsNullOrWhiteSpace(_coverImagePath);

        if (string.IsNullOrWhiteSpace(_coverImagePath) || !File.Exists(_coverImagePath))
        {
            CoverPreviewImage.Source = null;
            CoverPreviewImage.Visibility = Visibility.Collapsed;
            CoverEmptyState.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(_coverImagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            CoverPreviewImage.Source = bitmap;
            CoverPreviewImage.Visibility = Visibility.Visible;
            CoverEmptyState.Visibility = Visibility.Collapsed;
        }
        catch
        {
            CoverPreviewImage.Source = null;
            CoverPreviewImage.Visibility = Visibility.Collapsed;
            CoverEmptyState.Visibility = Visibility.Visible;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (DialogResult != true)
        {
            DeleteTemporaryCover();
        }
    }

    private string CommitCoverChanges()
    {
        if (string.IsNullOrWhiteSpace(_coverImagePath))
        {
            DeleteTemporaryCover();
            DeleteBookOwnedCoverFile(_originalCoverImagePath);
            return string.Empty;
        }

        if (IsTemporaryCoverPath(_coverImagePath))
        {
            Directory.CreateDirectory(LibraryStore.CoverDirectory);
            var finalCoverImagePath = Path.Combine(LibraryStore.CoverDirectory, $"{_book.Id:N}.png");
            File.Copy(_coverImagePath, finalCoverImagePath, overwrite: true);
            DeleteTemporaryCover();

            if (!PathsEqual(_originalCoverImagePath, finalCoverImagePath))
            {
                DeleteBookOwnedCoverFile(_originalCoverImagePath);
            }

            return finalCoverImagePath;
        }

        return _coverImagePath;
    }

    private static string CoverEditTempDirectory => Path.Combine(Path.GetTempPath(), "SeaS", "CoverEdits");

    private void DeleteTemporaryCover()
    {
        if (string.IsNullOrWhiteSpace(_temporaryCoverImagePath))
        {
            return;
        }

        TryDeleteFile(_temporaryCoverImagePath);
        _temporaryCoverImagePath = null;
    }

    private static bool IsTemporaryCoverPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var tempDirectory = Path.GetFullPath(CoverEditTempDirectory);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(tempDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void DeleteBookOwnedCoverFile(string path)
    {
        if (IsBookOwnedCoverPath(path))
        {
            File.Delete(path);
        }
    }

    private bool IsBookOwnedCoverPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var coverDirectory = Path.GetFullPath(LibraryStore.CoverDirectory);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(coverDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileNameWithoutExtension(fullPath), _book.Id.ToString("N"), StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
