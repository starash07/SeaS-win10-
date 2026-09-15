using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SeaS.App;

public partial class ImageCropWindow : Window
{
    private const int OutputWidth = 600;
    private const int OutputHeight = 800;

    private readonly BitmapSource _sourceBitmap;
    private readonly string _outputPath;
    private double _baseScale = 1;
    private double _zoom = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _isDragging;
    private Point _dragStart;
    private Point _dragStartOffset;

    public ImageCropWindow(string sourcePath, string outputPath)
    {
        InitializeComponent();
        DialogMotion.EnablePop(DialogRoot);
        _outputPath = outputPath;
        _sourceBitmap = LoadBitmap(sourcePath);
        SourceImage.Source = _sourceBitmap;
        Loaded += (_, _) => ResetImagePlacement();
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void ResetImagePlacement()
    {
        if (CropFrame.ActualWidth <= 0 || CropFrame.ActualHeight <= 0)
        {
            return;
        }

        _baseScale = Math.Max(
            CropFrame.ActualWidth / _sourceBitmap.PixelWidth,
            CropFrame.ActualHeight / _sourceBitmap.PixelHeight);
        _zoom = 1;
        ZoomSlider.Value = 1;
        _offsetX = (CropFrame.ActualWidth - _sourceBitmap.PixelWidth * CurrentScale) / 2;
        _offsetY = (CropFrame.ActualHeight - _sourceBitmap.PixelHeight * CurrentScale) / 2;
        ClampImageOffset();
        UpdateImagePlacement();
        UpdateZoomText();
    }

    private double CurrentScale => _baseScale * _zoom;

    private void UpdateImagePlacement()
    {
        SourceImage.Width = _sourceBitmap.PixelWidth * CurrentScale;
        SourceImage.Height = _sourceBitmap.PixelHeight * CurrentScale;
        Canvas.SetLeft(SourceImage, _offsetX);
        Canvas.SetTop(SourceImage, _offsetY);
    }

    private void UpdateZoomText()
    {
        ZoomValueText.Text = $"{_zoom:P0}";
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var oldScale = CurrentScale;
        var center = new Point(CropFrame.ActualWidth / 2, CropFrame.ActualHeight / 2);
        var sourceCenterX = (center.X - _offsetX) / oldScale;
        var sourceCenterY = (center.Y - _offsetY) / oldScale;
        _zoom = ZoomSlider.Value;
        _offsetX = center.X - sourceCenterX * CurrentScale;
        _offsetY = center.Y - sourceCenterY * CurrentScale;
        ClampImageOffset();
        UpdateImagePlacement();
        UpdateZoomText();
    }

    private void CropFrame_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + (e.Delta > 0 ? 0.08 : -0.08), ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    private void CropFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStart = e.GetPosition(CropFrame);
        _dragStartOffset = new Point(_offsetX, _offsetY);
        CropFrame.CaptureMouse();
    }

    private void CropFrame_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(CropFrame);
        _offsetX = _dragStartOffset.X + position.X - _dragStart.X;
        _offsetY = _dragStartOffset.Y + position.Y - _dragStart.Y;
        ClampImageOffset();
        UpdateImagePlacement();
    }

    private void CropFrame_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        CropFrame.ReleaseMouseCapture();
    }

    private void ClampImageOffset()
    {
        var scaledWidth = _sourceBitmap.PixelWidth * CurrentScale;
        var scaledHeight = _sourceBitmap.PixelHeight * CurrentScale;
        _offsetX = ClampOffset(_offsetX, CropFrame.ActualWidth, scaledWidth);
        _offsetY = ClampOffset(_offsetY, CropFrame.ActualHeight, scaledHeight);
    }

    private static double ClampOffset(double offset, double frameSize, double scaledSize)
    {
        if (scaledSize <= frameSize)
        {
            return (frameSize - scaledSize) / 2;
        }

        return Math.Clamp(offset, frameSize - scaledSize, 0);
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        ResetImagePlacement();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);

            var cropX = (int)Math.Round(-_offsetX / CurrentScale);
            var cropY = (int)Math.Round(-_offsetY / CurrentScale);
            var cropWidth = (int)Math.Round(CropFrame.ActualWidth / CurrentScale);
            var cropHeight = (int)Math.Round(CropFrame.ActualHeight / CurrentScale);
            cropX = Math.Clamp(cropX, 0, Math.Max(0, _sourceBitmap.PixelWidth - 1));
            cropY = Math.Clamp(cropY, 0, Math.Max(0, _sourceBitmap.PixelHeight - 1));
            cropWidth = Math.Clamp(cropWidth, 1, _sourceBitmap.PixelWidth - cropX);
            cropHeight = Math.Clamp(cropHeight, 1, _sourceBitmap.PixelHeight - cropY);

            var cropped = new CroppedBitmap(_sourceBitmap, new Int32Rect(cropX, cropY, cropWidth, cropHeight));
            var resized = new TransformedBitmap(
                cropped,
                new System.Windows.Media.ScaleTransform(
                    OutputWidth / (double)cropWidth,
                    OutputHeight / (double)cropHeight));
            resized.Freeze();

            using var stream = File.Create(_outputPath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(resized));
            encoder.Save(stream);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            SeaSMessageBox.Show(this, $"保存封面失败：{exception.Message}", "SeaS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
