using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SeaS.App.Models;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SeaS.App;

public partial class SettingsWindow : Window
{
    private static readonly Dictionary<string, string> ReservedShortcuts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl+F"] = "书内搜索",
        ["F2"] = "重命名选中书签",
        ["Delete"] = "删除选中书签",
        ["Ctrl+Left"] = "上一章",
        ["Ctrl+Right"] = "下一章",
        ["Home"] = "跳到章节开头",
        ["End"] = "跳到章节结尾",
        ["Esc"] = "关闭面板",
        ["Left"] = "上一页/上一章",
        ["Right"] = "下一页/下一章",
        ["PageUp"] = "上一页",
        ["PageDown"] = "下一页",
        ["Ctrl+W"] = "关闭窗口"
    };

    private ShortcutEditItem? _capturingItem;

    public ObservableCollection<ShortcutEditItem> ShortcutItems { get; } = [];
    public ReaderShortcutPreferences ShortcutPreferences { get; private set; }
    public string VersionText { get; }

    public SettingsWindow(ReaderShortcutPreferences preferences)
    {
        ShortcutPreferences = (preferences ?? new ReaderShortcutPreferences()).Clone();
        ShortcutPreferences.Normalize();
        VersionText = GetVersionText();

        InitializeComponent();
        DialogMotion.EnablePop(DialogRoot);
        DataContext = this;

        foreach (var definition in ReaderShortcutPreferences.Definitions)
        {
            ShortcutItems.Add(new ShortcutEditItem(
                definition.Action,
                definition.FunctionName,
                definition.DefaultShortcut,
                ShortcutPreferences.GetShortcut(definition.Action)));
        }

        SelectPage(showShortcuts: true);
    }

    private void SelectShortcuts_Click(object sender, RoutedEventArgs e)
    {
        SelectPage(showShortcuts: true);
    }

    private void SelectAbout_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        SelectPage(showShortcuts: false);
    }

    private void SelectPage(bool showShortcuts)
    {
        ShortcutsPage.Visibility = showShortcuts ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = showShortcuts ? Visibility.Collapsed : Visibility.Visible;
        ResetShortcutsButton.Visibility = showShortcuts ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsTabButton.Tag = showShortcuts ? "Active" : null;
        AboutTabButton.Tag = showShortcuts ? null : "Active";
    }

    private void ShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShortcutEditItem item })
        {
            return;
        }

        if (_capturingItem == item)
        {
            return;
        }

        Activate();

        if (sender is UIElement element)
        {
            element.Focus();
            Keyboard.Focus(element);
        }

        CancelCapture();
        _capturingItem = item;
        item.IsCapturing = true;
        ShortcutHintText.Text = "请按下新的快捷键。";
    }

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_capturingItem is null)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            CancelCapture();
            ShortcutHintText.Text = string.Empty;
            return;
        }

        if (!ShortcutGesture.TryFromKeyEvent(e, out var gesture))
        {
            ShortcutHintText.Text = "请按下完整快捷键。";
            return;
        }

        if (gesture.NeedsModifier())
        {
            ShortcutHintText.Text = "字母和数字需要搭配 Ctrl、Shift 或 Alt。";
            return;
        }

        var shortcut = gesture.ToDisplayString();
        if (!ValidateShortcut(shortcut, _capturingItem, out var message))
        {
            ShortcutHintText.Text = message;
            return;
        }

        _capturingItem.ShortcutText = shortcut;
        CancelCapture();
        ShortcutHintText.Text = string.Empty;
    }

    private bool ValidateShortcut(string shortcut, ShortcutEditItem currentItem, out string message)
    {
        if (ReservedShortcuts.TryGetValue(shortcut, out var fixedAction))
        {
            message = $"和已有快捷键“{fixedAction}”冲突。";
            return false;
        }

        var conflict = ShortcutItems.FirstOrDefault(item =>
            item != currentItem
            && string.Equals(item.ShortcutText, shortcut, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            message = $"和“{conflict.FunctionName}”冲突。";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        foreach (var item in ShortcutItems)
        {
            item.ShortcutText = item.DefaultShortcut;
        }

        ShortcutHintText.Text = string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        var preferences = new ReaderShortcutPreferences();
        foreach (var item in ShortcutItems)
        {
            preferences.SetShortcut(item.Action, item.ShortcutText);
        }

        preferences.Normalize();
        ShortcutPreferences = preferences;
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void CancelCapture()
    {
        if (_capturingItem is null)
        {
            return;
        }

        _capturingItem.IsCapturing = false;
        _capturingItem = null;
    }

    private static string GetVersionText()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "V1.1.0" : $"V{version.Major}.{version.Minor}.{version.Build}";
    }

    public sealed class ShortcutEditItem : INotifyPropertyChanged
    {
        private string _shortcutText;
        private bool _isCapturing;

        public ShortcutEditItem(
            ReaderShortcutAction action,
            string functionName,
            string defaultShortcut,
            string shortcutText)
        {
            Action = action;
            FunctionName = functionName;
            DefaultShortcut = defaultShortcut;
            _shortcutText = shortcutText;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ReaderShortcutAction Action { get; }
        public string FunctionName { get; }
        public string DefaultShortcut { get; }

        public string ShortcutText
        {
            get => _shortcutText;
            set
            {
                if (_shortcutText == value)
                {
                    return;
                }

                _shortcutText = value;
                OnPropertyChanged(nameof(ShortcutText));
                OnPropertyChanged(nameof(DisplayShortcut));
            }
        }

        public bool IsCapturing
        {
            get => _isCapturing;
            set
            {
                if (_isCapturing == value)
                {
                    return;
                }

                _isCapturing = value;
                OnPropertyChanged(nameof(IsCapturing));
                OnPropertyChanged(nameof(DisplayShortcut));
                OnPropertyChanged(nameof(CaptureTag));
            }
        }

        public string DisplayShortcut => IsCapturing ? "按下新快捷键" : ShortcutText;
        public string? CaptureTag => IsCapturing ? "Capturing" : null;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
