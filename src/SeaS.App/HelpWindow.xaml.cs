using System.Windows;
using System.Windows.Input;

namespace SeaS.App;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        DialogMotion.EnablePop(DialogRoot);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void HelpPageTab_Click(object sender, RoutedEventArgs e)
    {
        var showFishGuide = sender == FishGuideTab;

        SeaSGuideTab.IsChecked = !showFishGuide;
        FishGuideTab.IsChecked = showFishGuide;
        SeaSGuidePanel.Visibility = showFishGuide ? Visibility.Collapsed : Visibility.Visible;
        FishGuidePanel.Visibility = showFishGuide ? Visibility.Visible : Visibility.Collapsed;
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
}
