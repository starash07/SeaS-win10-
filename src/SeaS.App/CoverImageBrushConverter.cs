using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SeaS.App.Models;

namespace SeaS.App;

public sealed class CoverImageBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is BookItem book)
        {
            return CreateBrush(book.CoverImagePath, GetFallbackBrush(book));
        }

        if (value is string path && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return CreateBrush(path, Application.Current.TryFindResource("BookCardBrush") as Brush ?? Brushes.Transparent);
        }

        return Application.Current.TryFindResource("BookCardBrush") as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }

    private static Brush CreateBrush(string path, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return fallback;
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

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return fallback;
        }
    }

    private static Brush GetFallbackBrush(BookItem book)
    {
        return Application.Current.TryFindResource("BookCardBrush") as Brush
               ?? Brushes.Transparent;
    }
}
