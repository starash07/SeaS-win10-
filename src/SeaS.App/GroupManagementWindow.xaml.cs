using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SeaS.App.Models;

namespace SeaS.App;

public partial class GroupManagementWindow : Window
{
    private Point _dragStartPoint;

    public ObservableCollection<GroupEditItem> EditableGroups { get; } = [];
    public List<BookGroup> UpdatedGroups { get; private set; } = [];

    public GroupManagementWindow(IEnumerable<BookGroup> groups)
    {
        InitializeComponent();
        DataContext = this;
        foreach (var group in groups.OrderBy(group => group.SortOrder).ThenBy(group => group.CreatedAt))
        {
            EditableGroups.Add(new GroupEditItem
            {
                Id = group.Id,
                Name = group.Name,
                CreatedAt = group.CreatedAt
            });
        }

        RefreshEmptyHint();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var group in EditableGroups)
        {
            group.Name = group.Name.Trim();
            if (string.IsNullOrWhiteSpace(group.Name))
            {
                ErrorText.Text = "分组名称不能为空";
                return;
            }

            if (!names.Add(group.Name))
            {
                ErrorText.Text = "分组名称不能重复";
                return;
            }
        }

        UpdatedGroups = EditableGroups
            .Select((group, index) => new BookGroup
            {
                Id = group.Id,
                Name = group.Name,
                SortOrder = index,
                CreatedAt = group.CreatedAt
            })
            .ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GroupEditItem group)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            $"删除“{group.Name}”？\n该分组里的书籍会移到“未分组”，原文件不会被删除。",
            "SeaS",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        EditableGroups.Remove(group);
        RefreshEmptyHint();
    }

    private void StartEditGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GroupEditItem group)
        {
            return;
        }

        foreach (var item in EditableGroups)
        {
            item.IsEditing = ReferenceEquals(item, group);
        }

        if (FindAncestor<ListBoxItem>(sender as DependencyObject) is not { } listBoxItem)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (FindDescendant<TextBox>(listBoxItem) is not { } textBox)
                {
                    return;
                }

                textBox.Focus();
                textBox.SelectAll();
            }),
            DispatcherPriority.Input);
    }

    private void GroupNameEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupEditItem group)
        {
            group.IsEditing = false;
        }
    }

    private void GroupNameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter and not Key.Escape)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is GroupEditItem group)
        {
            group.IsEditing = false;
        }

        if (sender is TextBox textBox)
        {
            Keyboard.ClearFocus();
            textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        e.Handled = true;
    }

    private void GroupListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void GroupListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } item
            || item.DataContext is not GroupEditItem group)
        {
            return;
        }

        DragDrop.DoDragDrop(item, group, DragDropEffects.Move);
    }

    private void GroupListBox_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(GroupEditItem)))
        {
            return;
        }

        var droppedGroup = (GroupEditItem)e.Data.GetData(typeof(GroupEditItem))!;
        var targetItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetItem?.DataContext is not GroupEditItem targetGroup || ReferenceEquals(droppedGroup, targetGroup))
        {
            return;
        }

        var oldIndex = EditableGroups.IndexOf(droppedGroup);
        var newIndex = EditableGroups.IndexOf(targetGroup);
        if (oldIndex < 0 || newIndex < 0)
        {
            return;
        }

        EditableGroups.Move(oldIndex, newIndex);
    }

    private void RefreshEmptyHint()
    {
        EmptyHint.Visibility = EditableGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T target)
            {
                return target;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject current)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
        {
            var child = VisualTreeHelper.GetChild(current, index);
            if (child is T target)
            {
                return target;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}

public sealed class GroupEditItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private bool _isEditing;

    public Guid Id { get; init; }
    public DateTime CreatedAt { get; init; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
            {
                return;
            }

            _isEditing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
