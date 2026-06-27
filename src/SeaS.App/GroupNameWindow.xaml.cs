using System.Windows;
using System.Windows.Input;

namespace SeaS.App;

public partial class GroupNameWindow : Window
{
    private readonly HashSet<string> _existingNames;

    public string GroupName { get; private set; } = string.Empty;

    public GroupNameWindow(string title, IEnumerable<string> existingNames)
    {
        InitializeComponent();
        DialogTitle.Text = title;
        _existingNames = existingNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirm();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }

    private void Confirm()
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText.Text = "分组名称不能为空";
            return;
        }

        if (_existingNames.Contains(name))
        {
            ErrorText.Text = "已经有同名分组";
            return;
        }

        GroupName = name;
        DialogResult = true;
    }
}
