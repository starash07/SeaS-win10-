using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SeaS.App;

public partial class SeaSMessageBox : Window
{
    private readonly MessageBoxButton _buttons;
    private readonly bool _isDestructive;
    private readonly bool _distributeButtons;
    private MessageBoxResult _result;

    private SeaSMessageBox(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image,
        bool showCloseButton = true,
        bool distributeButtons = false)
    {
        InitializeComponent();
        DialogMotion.EnablePop(DialogRoot);

        _buttons = buttons;
        _isDestructive = IsDestructiveMessage(message, image);
        _distributeButtons = distributeButtons;
        _result = GetDefaultCloseResult(buttons);

        Title = string.IsNullOrWhiteSpace(caption) ? "SeaS" : caption;
        CaptionText.Text = Title;
        MessageText.Text = message;
        CloseButton.Visibility = showCloseButton ? Visibility.Visible : Visibility.Collapsed;

        ConfigureIcon(image);
        ConfigureButtons(buttons);
    }

    public static MessageBoxResult Show(
        Window owner,
        string message,
        string caption = "SeaS",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None,
        bool showCloseButton = true,
        bool distributeButtons = false)
    {
        var dialog = new SeaSMessageBox(message, caption, buttons, image, showCloseButton, distributeButtons)
        {
            Owner = owner
        };

        _ = dialog.ShowDialog();
        return dialog._result;
    }

    private void ConfigureIcon(MessageBoxImage image)
    {
        if (_isDestructive)
        {
            SetIcon("!", "#FFF0F0", "#C45D64");
            return;
        }

        var imageValue = (int)image;
        var (text, background, foreground) = imageValue switch
        {
            32 => ("?", "#EEF5F9", "#4E7185"),
            48 => ("!", "#FFF4E7", "#A96E31"),
            16 => ("!", "#FFF0F0", "#C45D64"),
            _ => ("i", "#EEF5F9", "#4E7185")
        };

        SetIcon(text, background, foreground);
    }

    private void SetIcon(string text, string background, string foreground)
    {
        var backgroundBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        var foregroundBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));

        IconBubble.Background = backgroundBrush;
        IconText.Foreground = foregroundBrush;
        WarningStem.Fill = foregroundBrush;
        WarningDot.Fill = foregroundBrush;

        var useWarningGlyph = text == "!";
        WarningGlyph.Visibility = useWarningGlyph ? Visibility.Visible : Visibility.Collapsed;
        IconText.Visibility = useWarningGlyph ? Visibility.Collapsed : Visibility.Visible;
        IconText.Text = text;
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        ButtonPanel.Children.Clear();

        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                AddButton("取消", MessageBoxResult.Cancel, "DialogSecondaryButtonStyle", isCancel: true);
                AddButton("确定", MessageBoxResult.OK, GetConfirmButtonStyle(), isDefault: true);
                break;
            case MessageBoxButton.YesNo:
                AddButton("否", MessageBoxResult.No, "DialogSecondaryButtonStyle", isCancel: true);
                AddButton("是", MessageBoxResult.Yes, GetConfirmButtonStyle(), isDefault: true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("取消", MessageBoxResult.Cancel, "DialogSecondaryButtonStyle", isCancel: true);
                AddButton("否", MessageBoxResult.No, "DialogSecondaryButtonStyle");
                AddButton("是", MessageBoxResult.Yes, GetConfirmButtonStyle(), isDefault: true);
                break;
            default:
                AddButton("确定", MessageBoxResult.OK, "DialogPrimaryButtonStyle", isDefault: true, isCancel: true);
                break;
        }

        if (_distributeButtons && ButtonPanel.Children.Count == 2)
        {
            ButtonPanel.ColumnDefinitions.Clear();
            ButtonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ButtonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ButtonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(ButtonPanel.Children[0], 0);
            Grid.SetColumn(ButtonPanel.Children[1], 2);
            ((FrameworkElement)ButtonPanel.Children[0]).Margin = new Thickness(0);
            ((FrameworkElement)ButtonPanel.Children[1]).Margin = new Thickness(0);
            ButtonPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }

    private string GetConfirmButtonStyle()
    {
        return _isDestructive ? "DialogDangerButtonStyle" : "DialogPrimaryButtonStyle";
    }

    private void AddButton(
        string text,
        MessageBoxResult result,
        string styleKey,
        bool isDefault = false,
        bool isCancel = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 82,
            Margin = ButtonPanel.Children.Count == 0 ? new Thickness(0) : new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource(styleKey),
            IsDefault = isDefault,
            IsCancel = isCancel
        };

        button.Click += (_, _) => CloseWithResult(result);
        ButtonPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(button, ButtonPanel.Children.Count);
        ButtonPanel.Children.Add(button);
    }

    private void CloseWithResult(MessageBoxResult result)
    {
        _result = result;
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithResult(GetDefaultCloseResult(_buttons));
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

    private static MessageBoxResult GetDefaultCloseResult(MessageBoxButton buttons)
    {
        return buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel
        };
    }

    private static bool IsDestructiveMessage(string message, MessageBoxImage image)
    {
        if ((int)image == 16)
        {
            return false;
        }

        return message.Contains("删除", StringComparison.Ordinal)
               || message.Contains("移除", StringComparison.Ordinal)
               || message.Contains("清理", StringComparison.Ordinal);
    }
}
