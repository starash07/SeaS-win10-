using System.Windows;
using System.Windows.Input;

namespace SeaS.App;

public enum MissingBookAction
{
    None,
    Relocate,
    CleanMissing
}

public partial class MissingBookActionWindow : Window
{
    public MissingBookActionWindow(string bookTitle)
    {
        InitializeComponent();
        DialogMotion.EnablePop(DialogRoot);
        BookTitle = $"《{bookTitle}》";
        DataContext = this;
    }

    public string BookTitle { get; }

    public MissingBookAction SelectedAction { get; private set; } = MissingBookAction.None;

    private void Relocate_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = MissingBookAction.Relocate;
        DialogResult = true;
    }

    private void CleanMissing_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = MissingBookAction.CleanMissing;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = MissingBookAction.None;
        DialogResult = false;
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
