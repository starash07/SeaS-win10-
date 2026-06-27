using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SeaS.App.Models;

public sealed class GroupSectionViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private bool _isEditing;

    public Guid? GroupId { get; init; }

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

    public bool IsUngrouped { get; init; }
    public bool IsExpanded { get; set; } = true;
    
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

    public ObservableCollection<BookItem> Books { get; } = [];

    public int BookCount => Books.Count;

    public string BookCountText => $"{BookCount} 本";

    public event PropertyChangedEventHandler? PropertyChanged;
}
