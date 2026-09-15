using System.Windows;
using Forms = System.Windows.Forms;

namespace SeaS.App;

public partial class TrayMenuWindow : Window
{
    private readonly Action _openAction;
    private readonly Action _importAction;
    private readonly Action _scanFolderAction;
    private readonly Action _modeAction;
    private readonly Action _exitAction;
    private bool _isExecutingAction;

    public TrayMenuWindow(
        string modeActionText,
        Action openAction,
        Action importAction,
        Action scanFolderAction,
        Action modeAction,
        Action exitAction)
    {
        InitializeComponent();
        DialogMotion.EnablePop(DialogRoot);

        ModeButtonText.Text = modeActionText;
        _openAction = openAction;
        _importAction = importAction;
        _scanFolderAction = scanFolderAction;
        _modeAction = modeAction;
        _exitAction = exitAction;
    }

    public void ShowNearCursor()
    {
        var cursor = Forms.Cursor.Position;
        Loaded += (_, _) => PositionNear(cursor.X, cursor.Y);
        Show();
        Activate();
    }

    private void PositionNear(double screenX, double screenY)
    {
        UpdateLayout();

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 230;
        var left = screenX + 10;
        var top = screenY - height - 10;

        var minLeft = SystemParameters.VirtualScreenLeft + 6;
        var minTop = SystemParameters.VirtualScreenTop + 6;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - width - 6;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height - 6;

        Left = Math.Clamp(left, minLeft, maxLeft);
        Top = Math.Clamp(top, minTop, maxTop);
    }

    private void Execute(Action action)
    {
        _isExecutingAction = true;
        Close();
        action();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        Execute(_openAction);
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        Execute(_importAction);
    }

    private void ScanFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Execute(_scanFolderAction);
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        Execute(_modeAction);
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Execute(_exitAction);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_isExecutingAction)
        {
            Close();
        }
    }
}
