using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using SeaS.App.Models;

namespace SeaS.App;

public partial class MissingBooksWindow : Window
{
    public ObservableCollection<MissingBookSelection> Items { get; }

    public List<BookItem> SelectedBooks { get; private set; } = [];

    public MissingBooksWindow(IEnumerable<BookItem> books)
    {
        InitializeComponent();
        Items = new ObservableCollection<MissingBookSelection>(
            books.Select(book => new MissingBookSelection(book)));
        DataContext = this;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        SelectedBooks = Items
            .Where(item => item.IsSelected)
            .Select(item => item.Book)
            .ToList();

        if (SelectedBooks.Count == 0)
        {
            MessageBox.Show(this, "请先选择需要移除的失效书籍。", "SeaS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}

public sealed class MissingBookSelection : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public MissingBookSelection(BookItem book)
    {
        Book = book;
    }

    public BookItem Book { get; }

    public string Title => $"《{Book.Title}》";

    public string FilePath => Book.FilePath;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
