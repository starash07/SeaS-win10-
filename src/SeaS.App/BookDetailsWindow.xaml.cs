using SeaS.App.Models;
using System.Windows;

namespace SeaS.App;

public partial class BookDetailsWindow : Window
{
    private readonly BookItem _book;

    public BookDetailsWindow(BookItem book)
    {
        InitializeComponent();
        _book = book;
        TitleBox.Text = book.Title;
        AuthorBox.Text = book.Author;
        TitleBox.SelectAll();
        TitleBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        var author = AuthorBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            MessageBox.Show(this, "书名不能为空。", "SeaS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _book.Title = title;
        _book.Author = string.IsNullOrWhiteSpace(author) ? "未知作者" : author;
        DialogResult = true;
    }
}
