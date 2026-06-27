using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using SeaS.App.Models;
using SeaS.App.Services;
using Forms = System.Windows.Forms;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SeaS.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly HashSet<string> AllBooksSortModes =
    [
        "recent",
        "added",
        "title",
        "author",
        "progress"
    ];

    private readonly LibraryState _state;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _windowPlacementTimer;
    private string _activeFilter = "all";
    private bool _isBulkMode;
    private bool _suppressLibraryToolEvents;
    private bool _sidebarExpanded;
    private bool _exitRequested;
    private bool _isHiddenToTray;
    private WindowState _windowStateBeforeTray = WindowState.Normal;
    private BookItem? _contextBook;
    private BookItem? _selectedBook;
    private Forms.NotifyIcon? _trayIcon;
    private bool _windowPlacementReady;

    public ObservableCollection<BookItem> ShelfBooks { get; } = [];
    public ObservableCollection<BookItem> RecentBooks { get; } = [];
    public ObservableCollection<GroupSectionViewModel> GroupSections { get; } = [];

    public bool IsBulkMode
    {
        get => _isBulkMode;
        private set
        {
            if (_isBulkMode == value)
            {
                return;
            }

            _isBulkMode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBulkMode)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        InitializeTrayIcon();
        System.Windows.Application.Current.SessionEnding += (_, _) => _exitRequested = true;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };

        _windowPlacementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _windowPlacementTimer.Tick += (_, _) =>
        {
            _windowPlacementTimer.Stop();
            SaveWindowPlacement();
        };

        _state = LibraryStore.Load();
        RestoreWindowPlacement();
        _windowPlacementReady = true;
        LocationChanged += (_, _) => ScheduleWindowPlacementSave();
        SizeChanged += (_, _) => ScheduleWindowPlacementSave();
        var metadataChanged = NormalizeLibraryState();
        foreach (var book in _state.Books)
        {
            book.RefreshFileState();
            metadataChanged |= BookMetadata.Normalize(book);
        }

        if (metadataChanged)
        {
            SaveState();
        }

        SetMode(_state.Mode == "fish" ? "fish" : "leisure", save: false);
        InitializeLibraryToolState();
        RefreshViews();
    }

    private void InitializeLibraryToolState()
    {
        if (!AllBooksSortModes.Contains(_state.AllBooksSortMode))
        {
            _state.AllBooksSortMode = "recent";
            _state.AllBooksSortDescending = true;
            SaveState();
        }

        _suppressLibraryToolEvents = true;
        SortModeComboBox.SelectedValue = _state.AllBooksSortMode;
        UpdateSortDirectionVisual();
        _suppressLibraryToolEvents = false;
        UpdateBulkSelectionStatus();
    }

    private void RefreshViews()
    {
        var keyword = SearchBox.Text.Trim();
        var searchedBooks = _state.Books
            .Where(book => MatchesSearch(book, keyword))
            .ToList();

        ShelfBooks.Clear();
        GroupSections.Clear();

        if (_activeFilter == "groups")
        {
            BuildGroupSections(searchedBooks, string.IsNullOrWhiteSpace(keyword));
        }
        else
        {
            var matchingBooks = _activeFilter switch
            {
                "recent" => searchedBooks
                    .Where(book => book.LastOpenedAt.HasValue)
                    .OrderByDescending(book => book.LastOpenedAt)
                    .ThenByDescending(book => book.AddedAt)
                    .Take(10),
                "favorite" => searchedBooks
                    .Where(book => book.IsFavorite)
                    .OrderByDescending(book => book.FavoritedAt ?? book.AddedAt)
                    .ThenByDescending(book => book.AddedAt),
                _ => ApplyAllBooksSort(searchedBooks)
            };

            foreach (var book in matchingBooks)
            {
                ShelfBooks.Add(book);
            }
        }

        var isGroupedView = _activeFilter == "groups";
        var hasContent = isGroupedView ? GroupSections.Count > 0 : ShelfBooks.Count > 0;
        ShelfItemsControl.Visibility = !isGroupedView && ShelfBooks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupSectionsItemsControl.Visibility = isGroupedView && GroupSections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        ShelfCountText.Text = $"共 {(isGroupedView ? GroupSections.Sum(section => section.BookCount) : ShelfBooks.Count)} 本";
        UpdateTopNavigationVisuals();
        UpdateLibraryToolVisuals();

        var isLibraryEmpty = _state.Books.Count == 0;
        if (isLibraryEmpty)
        {
            EmptyStateTitle.Text = "书架还是空的";
            EmptyStateHint.Text = "拖入 TXT 文件，或从左侧导入";
        }
        else if (!string.IsNullOrWhiteSpace(keyword))
        {
            EmptyStateTitle.Text = "没有找到匹配的书籍";
            EmptyStateHint.Text = "换个关键词试试";
        }
        else
        {
            EmptyStateTitle.Text = _activeFilter switch
            {
                "recent" => "还没有最近阅读",
                "favorite" => "还没有收藏",
                "groups" => "还没有分组",
                _ => "没有找到匹配的书籍"
            };
            EmptyStateHint.Text = _activeFilter switch
            {
                "recent" => "打开一本书后会出现在这里，最多显示 10 本",
                "favorite" => "在书籍右键菜单里加入收藏",
                "groups" => "点击上方“添加分组”开始整理书籍",
                _ => "换个筛选条件试试"
            };
        }
    }

    private bool NormalizeLibraryState()
    {
        var changed = false;

        if (_state.Books is null)
        {
            _state.Books = [];
            changed = true;
        }

        if (_state.Groups is null)
        {
            _state.Groups = [];
            changed = true;
        }

        var seenGroupIds = new HashSet<Guid>();
        var normalizedGroups = _state.Groups
            .Where(group => group.Id != Guid.Empty && !string.IsNullOrWhiteSpace(group.Name) && seenGroupIds.Add(group.Id))
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.CreatedAt)
            .ToList();

        if (normalizedGroups.Count != _state.Groups.Count)
        {
            changed = true;
        }

        for (var index = 0; index < normalizedGroups.Count; index++)
        {
            if (normalizedGroups[index].SortOrder != index)
            {
                normalizedGroups[index].SortOrder = index;
                changed = true;
            }
        }

        _state.Groups = normalizedGroups;
        var validGroupIds = _state.Groups.Select(group => group.Id).ToHashSet();
        foreach (var book in _state.Books)
        {
            if (book.GroupId.HasValue && !validGroupIds.Contains(book.GroupId.Value))
            {
                book.GroupId = null;
                changed = true;
            }

            if (book.IsFavorite && book.FavoritedAt is null)
            {
                book.FavoritedAt = book.LastOpenedAt ?? book.AddedAt;
                changed = true;
            }

            if (!book.IsFavorite && book.FavoritedAt is not null)
            {
                book.FavoritedAt = null;
                changed = true;
            }
        }

        return changed;
    }

    private void BuildGroupSections(List<BookItem> searchedBooks, bool includeEmptyGroups)
    {
        var orderedGroups = _state.Groups
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.CreatedAt)
            .ToList();

        foreach (var group in orderedGroups)
        {
            var books = OrderByReadingActivity(searchedBooks.Where(book => book.GroupId == group.Id)).ToList();
            if (books.Count == 0 && !includeEmptyGroups)
            {
                continue;
            }

            var section = new GroupSectionViewModel
            {
                GroupId = group.Id,
                Name = group.Name
            };

            foreach (var book in books)
            {
                section.Books.Add(book);
            }

            GroupSections.Add(section);
        }

        var ungroupedBooks = OrderByReadingActivity(searchedBooks.Where(book => book.GroupId is null)).ToList();
        if (ungroupedBooks.Count > 0)
        {
            var section = new GroupSectionViewModel
            {
                Name = "未分组",
                IsUngrouped = true
            };

            foreach (var book in ungroupedBooks)
            {
                section.Books.Add(book);
            }

            GroupSections.Add(section);
        }
    }

    private static IOrderedEnumerable<BookItem> OrderByReadingActivity(IEnumerable<BookItem> books)
    {
        return books
            .OrderByDescending(book => book.LastOpenedAt.HasValue)
            .ThenByDescending(book => book.LastOpenedAt ?? book.AddedAt)
            .ThenByDescending(book => book.AddedAt);
    }

    private IOrderedEnumerable<BookItem> ApplyAllBooksSort(IEnumerable<BookItem> books)
    {
        var descending = _state.AllBooksSortDescending;
        return _state.AllBooksSortMode switch
        {
            "added" => descending
                ? books.OrderByDescending(book => book.AddedAt).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
                : books.OrderBy(book => book.AddedAt).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            "title" => descending
                ? books.OrderByDescending(book => book.Title, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(book => book.AddedAt)
                : books.OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(book => book.AddedAt),
            "author" => descending
                ? books.OrderByDescending(book => book.Author, StringComparer.CurrentCultureIgnoreCase).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
                : books.OrderBy(book => book.Author, StringComparer.CurrentCultureIgnoreCase).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            "progress" => descending
                ? books.OrderByDescending(book => book.LastReadChapterIndex)
                    .ThenByDescending(book => book.LastReadChapterProgress)
                    .ThenByDescending(book => book.LastOpenedAt ?? book.AddedAt)
                : books.OrderBy(book => book.LastReadChapterIndex)
                    .ThenBy(book => book.LastReadChapterProgress)
                    .ThenByDescending(book => book.LastOpenedAt ?? book.AddedAt),
            _ => descending
                ? OrderByReadingActivity(books)
                : books.OrderBy(book => book.LastOpenedAt.HasValue)
                    .ThenBy(book => book.LastOpenedAt ?? book.AddedAt)
                    .ThenBy(book => book.AddedAt)
        };
    }

    private static bool MatchesSearch(BookItem book, string keyword)
    {
        return string.IsNullOrWhiteSpace(keyword)
               || book.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
               || book.Author.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool MatchesFilter(BookItem book)
    {
        return _activeFilter switch
        {
            "recent" => book.LastOpenedAt.HasValue,
            "favorite" => book.IsFavorite,
            _ => true
        };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateSearchPlaceholderVisibility();
        RefreshViews();
    }

    private void SearchBox_FocusChanged(object sender, RoutedEventArgs e)
    {
        UpdateSearchPlaceholderVisibility();
    }

    private void UpdateSearchPlaceholderVisibility()
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) && !SearchBox.IsKeyboardFocusWithin
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void TopViewTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton selected)
        {
            return;
        }

        _activeFilter = selected.CommandParameter?.ToString() ?? "all";
        if (_activeFilter != "all")
        {
            SetBulkMode(false);
        }

        SelectSidebarFilter("all");
        RefreshViews();
    }

    private void NavFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton selected)
        {
            return;
        }

        _activeFilter = selected.CommandParameter?.ToString() ?? "all";
        if (_activeFilter != "all")
        {
            SetBulkMode(false);
        }

        SelectSidebarFilter(_activeFilter == "recent" ? "recent" : "all");
        if (_activeFilter == "all")
        {
            SelectTopTab("all");
        }

        RefreshViews();
    }

    private void SelectSidebarFilter(string filter)
    {
        foreach (var toggle in SidebarNavigation.Children.OfType<ToggleButton>())
        {
            toggle.IsChecked = string.Equals(toggle.CommandParameter?.ToString(), filter, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void SelectTopTab(string filter)
    {
        foreach (var toggle in new[] { AllBooksTab, FavoriteBooksTab, GroupedBooksTab })
        {
            toggle.IsChecked = string.Equals(toggle.CommandParameter?.ToString(), filter, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void UpdateTopNavigationVisuals()
    {
        var isRecent = _activeFilter == "recent";
        var isGrouped = _activeFilter == "groups";
        ShelfTabsPanel.Visibility = isRecent ? Visibility.Collapsed : Visibility.Visible;
        RecentViewTitle.Visibility = isRecent ? Visibility.Visible : Visibility.Collapsed;
        GroupPageActionsPanel.Visibility = isGrouped ? Visibility.Visible : Visibility.Collapsed;

        if (!isRecent)
        {
            SelectTopTab(_activeFilter);
        }

        SelectSidebarFilter(isRecent ? "recent" : "all");
    }

    private void UpdateLibraryToolVisuals()
    {
        var shouldShowLibraryTools = _activeFilter == "all" && _state.Books.Count > 0;
        LibraryToolsPanel.Visibility = shouldShowLibraryTools ? Visibility.Visible : Visibility.Collapsed;
        LibraryNormalToolsPanel.Visibility = IsBulkMode ? Visibility.Collapsed : Visibility.Visible;
        BulkActionsPanel.Visibility = IsBulkMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateBulkSelectionStatus();
    }

    private void SortModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLibraryToolEvents || SortModeComboBox.SelectedValue is not string mode || !AllBooksSortModes.Contains(mode))
        {
            return;
        }

        _state.AllBooksSortMode = mode;
        SaveState();
        RefreshViews();
    }

    private void SortDirectionButton_Click(object sender, RoutedEventArgs e)
    {
        _state.AllBooksSortDescending = !_state.AllBooksSortDescending;
        UpdateSortDirectionVisual();
        SaveState();
        RefreshViews();
    }

    private void UpdateSortDirectionVisual()
    {
        SortDirectionText.Text = _state.AllBooksSortDescending ? "降序" : "升序";
        SortDirectionIcon.Text = _state.AllBooksSortDescending ? "\uE74B" : "\uE74A";
    }

    private void EnterBulkMode_Click(object sender, RoutedEventArgs e)
    {
        SetBulkMode(true);
    }

    private void ExitBulkMode_Click(object sender, RoutedEventArgs e)
    {
        SetBulkMode(false);
    }

    private void SetBulkMode(bool enabled)
    {
        IsBulkMode = enabled && _activeFilter == "all";
        if (!IsBulkMode)
        {
            ClearBatchSelections(updateStatus: false);
        }

        UpdateLibraryToolVisuals();
    }

    private void ClearBatchSelections(bool updateStatus = true)
    {
        foreach (var book in _state.Books.Where(book => book.IsBatchSelected))
        {
            book.IsBatchSelected = false;
        }

        if (updateStatus)
        {
            UpdateBulkSelectionStatus();
        }
    }

    private void UpdateBulkSelectionStatus()
    {
        if (!IsInitialized)
        {
            return;
        }

        var selectedCount = _state.Books.Count(book => book.IsBatchSelected);
        BulkSelectionStatusText.Text = $"已选 {selectedCount} 本";
    }

    private List<BookItem> GetSelectedBooks()
    {
        return _state.Books.Where(book => book.IsBatchSelected).ToList();
    }

    private List<BookItem>? GetSelectedBooksOrNotify()
    {
        var selectedBooks = GetSelectedBooks();
        if (selectedBooks.Count == 0)
        {
            ShowToast("先选择需要操作的书籍");
            return null;
        }

        return selectedBooks;
    }

    private void SelectCurrentResults_Click(object sender, RoutedEventArgs e)
    {
        if (!IsBulkMode)
        {
            SetBulkMode(true);
        }

        foreach (var book in ShelfBooks)
        {
            book.IsBatchSelected = true;
        }

        UpdateBulkSelectionStatus();
    }

    private void ClearBulkSelection_Click(object sender, RoutedEventArgs e)
    {
        ClearBatchSelections();
    }

    private void BulkAddFavorite_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBooksOrNotify();
        if (selectedBooks is null)
        {
            return;
        }

        foreach (var book in selectedBooks)
        {
            book.IsFavorite = true;
            book.FavoritedAt ??= DateTime.Now;
        }

        SaveState();
        RefreshViews();
        ShowToast($"已收藏 {selectedBooks.Count} 本书");
    }

    private void BulkRemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBooksOrNotify();
        if (selectedBooks is null)
        {
            return;
        }

        foreach (var book in selectedBooks)
        {
            book.IsFavorite = false;
            book.FavoritedAt = null;
        }

        SaveState();
        RefreshViews();
        ShowToast($"已取消收藏 {selectedBooks.Count} 本书");
    }

    private void BulkMoveGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedBooksOrNotify() is null)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = BulkMoveGroupButton,
            Placement = PlacementMode.Bottom
        };

        var ungroupedItem = new MenuItem
        {
            Header = "未分组",
            Tag = null
        };
        ungroupedItem.Click += BulkMoveToGroup_Click;
        menu.Items.Add(ungroupedItem);

        if (_state.Groups.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var group in _state.Groups.OrderBy(group => group.SortOrder).ThenBy(group => group.CreatedAt))
            {
                var groupItem = new MenuItem
                {
                    Header = group.Name,
                    Tag = group.Id
                };
                groupItem.Click += BulkMoveToGroup_Click;
                menu.Items.Add(groupItem);
            }
        }
        else
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = "还没有自定义分组",
                IsEnabled = false
            });
        }

        menu.IsOpen = true;
    }

    private void BulkMoveToGroup_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBooksOrNotify();
        if (selectedBooks is null)
        {
            return;
        }

        var groupId = (sender as FrameworkElement)?.Tag is Guid id ? id : (Guid?)null;
        foreach (var book in selectedBooks)
        {
            book.GroupId = groupId;
        }

        SaveState();
        RefreshViews();
        ShowToast(groupId is null ? $"已设为未分组：{selectedBooks.Count} 本" : $"已移动到分组：{selectedBooks.Count} 本");
    }

    private void BulkUngroup_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBooksOrNotify();
        if (selectedBooks is null)
        {
            return;
        }

        foreach (var book in selectedBooks)
        {
            book.GroupId = null;
        }

        SaveState();
        RefreshViews();
        ShowToast($"已设为未分组：{selectedBooks.Count} 本");
    }

    private void BulkRemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBooksOrNotify();
        if (selectedBooks is null)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            $"从书架移除选中的 {selectedBooks.Count} 本书？\n原 TXT 文件不会被删除。",
            "SeaS",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        foreach (var book in selectedBooks)
        {
            _state.Books.Remove(book);
        }

        SetBulkMode(false);
        SaveState();
        RefreshViews();
        ShowToast($"已从书架移除 {selectedBooks.Count} 本书");
    }

    private void CheckMissingBooks_Click(object sender, RoutedEventArgs e)
    {
        var missingBooks = new List<BookItem>();
        foreach (var book in _state.Books)
        {
            book.RefreshFileState();
            if (book.IsMissing)
            {
                missingBooks.Add(book);
            }
        }

        RefreshViews();
        if (missingBooks.Count == 0)
        {
            ShowToast("没有发现失效书籍");
            return;
        }

        var dialog = new MissingBooksWindow(missingBooks)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedBooks.Count == 0)
        {
            return;
        }

        foreach (var book in dialog.SelectedBooks)
        {
            _state.Books.Remove(book);
        }

        SaveState();
        RefreshViews();
        ShowToast($"已移除 {dialog.SelectedBooks.Count} 本失效书籍");
    }

    private void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 TXT 文件",
            Filter = "TXT 文本文件 (*.txt)|*.txt",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            ImportPaths(dialog.FileNames);
        }
    }

    private void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含 TXT 书籍的文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            ImportPaths([dialog.FolderName]);
        }
    }

    private void ImportPaths(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(path);
                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                files.AddRange(Directory.EnumerateFiles(path, "*.txt", SearchOption.AllDirectories));
            }
            catch (UnauthorizedAccessException)
            {
                ShowToast("部分文件夹无权访问，已跳过");
            }
            catch (IOException)
            {
                ShowToast("扫描文件夹时遇到无法读取的路径");
            }
        }

        var knownPaths = new HashSet<string>(
            _state.Books.Select(book => Path.GetFullPath(book.FilePath)),
            StringComparer.OrdinalIgnoreCase);

        var addedCount = 0;
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(file);
            if (!knownPaths.Add(fullPath))
            {
                continue;
            }

            var fileInfo = new FileInfo(fullPath);
            var (title, author) = BookMetadata.FromFileName(Path.GetFileNameWithoutExtension(fullPath));
            _state.Books.Add(new BookItem
            {
                Title = title,
                Author = author,
                FilePath = fullPath,
                FileSize = fileInfo.Length,
                AddedAt = DateTime.Now
            });
            addedCount++;
        }

        SaveState();
        RefreshViews();
        ShowToast(addedCount > 0 ? $"已导入 {addedCount} 本书" : "没有发现新的 TXT 文件");
    }

    private void Window_PreviewDragOver(object sender, WpfDragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, WpfDragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            ImportPaths(paths);
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var editingSection = GroupSections.FirstOrDefault(section => section.IsEditing);
        if (editingSection is null)
        {
            return;
        }

        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is { DataContext: GroupSectionViewModel editorSection }
            && ReferenceEquals(editorSection, editingSection))
        {
            return;
        }

        CommitGroupSectionNameEdit(editingSection);
    }

    private void BookCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BookItem book)
        {
            if (IsBulkMode && _activeFilter == "all")
            {
                book.IsBatchSelected = !book.IsBatchSelected;
                UpdateBulkSelectionStatus();
                return;
            }

            _selectedBook = book;
            OpenBook(book, _state.Mode == "fish");
        }
    }

    private void BookContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        _contextBook = contextMenu.DataContext as BookItem;
        PopulateGroupMenu(contextMenu);
    }

    private void PopulateGroupMenu(ContextMenu contextMenu)
    {
        var moveMenu = contextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => item.Name == "MoveGroupMenuItem");
        if (moveMenu is null || _contextBook is null)
        {
            return;
        }

        moveMenu.Items.Clear();

        var ungroupedItem = new MenuItem
        {
            Header = "未分组",
            IsCheckable = true,
            IsChecked = _contextBook.GroupId is null,
            Tag = null
        };
        ungroupedItem.Click += MoveBookToGroup_Click;
        moveMenu.Items.Add(ungroupedItem);

        if (_state.Groups.Count > 0)
        {
            moveMenu.Items.Add(new Separator());
        }
        else
        {
            moveMenu.Items.Add(new Separator());
            moveMenu.Items.Add(new MenuItem
            {
                Header = "还没有自定义分组",
                IsEnabled = false
            });
            return;
        }

        foreach (var group in _state.Groups.OrderBy(group => group.SortOrder).ThenBy(group => group.CreatedAt))
        {
            var groupItem = new MenuItem
            {
                Header = group.Name,
                IsCheckable = true,
                IsChecked = _contextBook.GroupId == group.Id,
                Tag = group.Id
            };
            groupItem.Click += MoveBookToGroup_Click;
            moveMenu.Items.Add(groupItem);
        }
    }

    private void MoveBookToGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuBook(sender) is not { } book)
        {
            return;
        }

        book.GroupId = (sender as FrameworkElement)?.Tag is Guid groupId ? groupId : null;
        SaveState();
        RefreshViews();
        ShowToast(book.GroupId is null ? "已移动到未分组" : "已移动到分组");
    }

    private BookItem? ResolveMenuBook(object sender)
    {
        return (sender as FrameworkElement)?.DataContext as BookItem ?? _contextBook ?? _selectedBook;
    }

    private void OpenNormally_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuBook(sender) is { } book)
        {
            OpenBook(book, useLittleFish: false);
        }
    }

    private void OpenWithLittleFish_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuBook(sender) is { } book)
        {
            OpenBook(book, useLittleFish: true);
        }
    }

    private void OpenBook(BookItem book, bool useLittleFish)
    {
        book.RefreshFileState();
        if (book.IsMissing)
        {
            RefreshViews();
            MessageBox.Show(this, "找不到这本书的原文件。可以定位新文件后重新导入，或从书架移除。", "SeaS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (useLittleFish)
        {
            var executablePath = FindBundledLittleFishExecutable();
            if (executablePath is null)
            {
                MessageBox.Show(this, "SeaS 内置的 LittleFish 文件缺失，请重新安装或修复 SeaS。", "SeaS", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
                };
                startInfo.ArgumentList.Add(book.FilePath);
                if (Process.Start(startInfo) is null)
                {
                    throw new InvalidOperationException("LittleFish 进程未能启动。");
                }

                MarkOpened(book);
                HideToTray();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"使用 LittleFish 打开失败：{exception.Message}", "SeaS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return;
        }

        try
        {
            var reader = new EmbeddedReaderControl(book, _state, SaveState);
            reader.BackRequested += (_, _) => CloseReader();
            reader.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
            reader.CloseRequested += (_, _) => Close();

            ReaderHost.Content = reader;
            ReaderHost.Visibility = Visibility.Visible;
            ShelfView.Visibility = Visibility.Collapsed;
            SidebarBorder.Visibility = Visibility.Collapsed;
            MarkOpened(book);
            reader.Focus();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"打开 TXT 阅读器失败：{exception.Message}", "SeaS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseReader()
    {
        ReaderHost.Content = null;
        ReaderHost.Visibility = Visibility.Collapsed;
        ShelfView.Visibility = Visibility.Visible;
        SidebarBorder.Visibility = Visibility.Visible;
        SetMode(_state.Mode == "fish" ? "fish" : "leisure", save: false);
    }

    private static string? FindBundledLittleFishExecutable()
    {
        var executablePath = Path.Combine(
            AppContext.BaseDirectory,
            "Plugins",
            "LittleFish",
            "LittleFish.exe");
        return File.Exists(executablePath) ? executablePath : null;
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var restoreItem = new Forms.ToolStripMenuItem("打开 SeaS");
        restoreItem.Click += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
        var exitItem = new Forms.ToolStripMenuItem("退出 SeaS");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(ExitFromTray);
        menu.Items.Add(restoreItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        var executablePath = Environment.ProcessPath;
        var icon = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath)
            ? System.Drawing.Icon.ExtractAssociatedIcon(executablePath)
            : null;

        _trayIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "SeaS 阅读器",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
    }

    private void HideToTray()
    {
        SaveWindowPlacement();
        _windowStateBeforeTray = WindowState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        _isHiddenToTray = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        if (_isHiddenToTray)
        {
            ShowInTaskbar = true;
            Show();
            WindowState = _windowStateBeforeTray;
            _isHiddenToTray = false;
        }
        else if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
    }

    internal void ActivateFromExternalRequest()
    {
        RestoreFromTray();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    private void MarkOpened(BookItem book)
    {
        book.LastOpenedAt = DateTime.Now;
        book.OpenCount++;
        SaveState();
        RefreshViews();
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuBook(sender) is not { } book)
        {
            return;
        }

        book.IsFavorite = !book.IsFavorite;
        book.FavoritedAt = book.IsFavorite ? DateTime.Now : null;
        SaveState();
        RefreshViews();
        ShowToast(book.IsFavorite ? "已加入收藏" : "已取消收藏");
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GroupNameWindow("添加分组", _state.Groups.Select(group => group.Name))
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _state.Groups.Add(new BookGroup
        {
            Name = dialog.GroupName,
            SortOrder = _state.Groups.Count,
            CreatedAt = DateTime.Now
        });
        _activeFilter = "groups";
        SaveState();
        RefreshViews();
        ShowToast("已添加分组");
    }

    private void EditGroups_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GroupManagementWindow(_state.Groups)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var remainingGroupIds = dialog.UpdatedGroups.Select(group => group.Id).ToHashSet();
        foreach (var book in _state.Books.Where(book => book.GroupId.HasValue && !remainingGroupIds.Contains(book.GroupId.Value)))
        {
            book.GroupId = null;
        }

        _state.Groups = dialog.UpdatedGroups;
        SaveState();
        RefreshViews();
        ShowToast("分组已更新");
    }

    private void GroupSectionEditIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not GroupSectionViewModel section
            || section.IsUngrouped
            || section.GroupId is null)
        {
            return;
        }

        foreach (var groupSection in GroupSections)
        {
            groupSection.IsEditing = ReferenceEquals(groupSection, section);
        }

        if (FindAncestor<Expander>(sender as DependencyObject) is not { } expander)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (FindDescendant<TextBox>(expander) is not { } editor)
                {
                    return;
                }

                editor.Focus();
                editor.SelectAll();
            }),
            DispatcherPriority.Input);
    }

    private void GroupSectionNameEditor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox editor && !editor.IsKeyboardFocusWithin)
        {
            editor.Focus();
            e.Handled = true;
        }
    }

    private void GroupSectionNameEditor_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key is not Key.Enter and not Key.Escape)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is GroupSectionViewModel section)
        {
            CommitGroupSectionNameEdit(section);
        }

        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void GroupSectionNameEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupSectionViewModel section)
        {
            CommitGroupSectionNameEdit(section);
        }
    }

    private void CommitGroupSectionNameEdit(GroupSectionViewModel section)
    {
        if (section.IsUngrouped || section.GroupId is null)
        {
            section.IsEditing = false;
            return;
        }

        var group = _state.Groups.FirstOrDefault(item => item.Id == section.GroupId.Value);
        if (group is null)
        {
            section.IsEditing = false;
            return;
        }

        var originalName = group.Name;
        var newName = section.Name.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            section.Name = originalName;
            section.IsEditing = false;
            ShowToast("分组名称不能为空");
            return;
        }

        if (_state.Groups.Any(item => item.Id != group.Id && string.Equals(item.Name, newName, StringComparison.CurrentCultureIgnoreCase)))
        {
            section.Name = originalName;
            section.IsEditing = false;
            ShowToast("已经有同名分组");
            return;
        }

        section.Name = newName;
        section.IsEditing = false;
        if (!string.Equals(group.Name, newName, StringComparison.Ordinal))
        {
            group.Name = newName;
            SaveState();
            ShowToast("分组名称已更新");
        }
    }

    private void EditBook_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuBook(sender) is not { } book)
        {
            return;
        }

        var dialog = new BookDetailsWindow(book) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            SaveState();
            RefreshViews();
        }
    }

    private void RevealBook_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuBook(sender) is not { } book)
        {
            return;
        }

        book.RefreshFileState();
        if (book.IsMissing)
        {
            RefreshViews();
            ShowToast("原文件已经不存在");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{book.FilePath}\"") { UseShellExecute = true });
    }

    private void RemoveBook_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuBook(sender) is not { } book)
        {
            return;
        }

        var result = MessageBox.Show(this, $"从书架移除《{book.Title}》？\n原文件不会被删除。", "SeaS", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        _state.Books.Remove(book);
        SaveState();
        RefreshViews();
        ShowToast("已从书架移除");
    }

    private void ModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetMode(_state.Mode == "fish" ? "leisure" : "fish", save: true);
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (ReaderHost.Visibility == Visibility.Visible)
        {
            return;
        }

        var position = e.GetPosition(WindowBorder);
        if (!_sidebarExpanded && position.X >= 0 && position.X <= 68)
        {
            ExpandSidebar();
        }
        else if (_sidebarExpanded && position.X > 176)
        {
            CollapseSidebar();
        }
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ReaderHost.Visibility == Visibility.Visible)
        {
            return;
        }

        CollapseSidebar();
    }

    private void ExpandSidebar()
    {
        if (_sidebarExpanded)
        {
            return;
        }

        _sidebarExpanded = true;
        SetBrush("SidebarSelectedLabelBrush", "#C6F7BD");
        System.Windows.Application.Current.Resources["SidebarSelectedMargin"] = new Thickness(0, 0, 20, 0);
        AnimateSidebarControls(144, 144, 190, EasingMode.EaseOut);

        SidebarBorder.BeginAnimation(
            FrameworkElement.WidthProperty,
            new DoubleAnimation(SidebarBorder.ActualWidth, 176, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void CollapseSidebar()
    {
        if (!_sidebarExpanded)
        {
            return;
        }

        _sidebarExpanded = false;
        SetBrush("SidebarSelectedLabelBrush", "#00C6F7BD");
        System.Windows.Application.Current.Resources["SidebarSelectedMargin"] = new Thickness(0);
        AnimateSidebarControls(36, 57, 160, EasingMode.EaseInOut);
        var sidebarAnimation = new DoubleAnimation(SidebarBorder.ActualWidth, 68, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        SidebarBorder.BeginAnimation(
            FrameworkElement.WidthProperty,
            sidebarAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateSidebarControls(double navigationWidth, double modeWidth, int durationMs, EasingMode easingMode)
    {
        foreach (var item in SidebarNavigation.Children.OfType<FrameworkElement>())
        {
            AnimateWidth(item, navigationWidth, durationMs, easingMode);
        }

        AnimateWidth(SettingsButton, navigationWidth, durationMs, easingMode);
        AnimateWidth(ModeToggleButton, modeWidth, durationMs, easingMode);
    }

    private static void AnimateWidth(FrameworkElement element, double targetWidth, int durationMs, EasingMode easingMode)
    {
        element.BeginAnimation(
            FrameworkElement.WidthProperty,
            new DoubleAnimation(element.ActualWidth, targetWidth, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = easingMode }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void SetMode(string mode, bool save)
    {
        _state.Mode = mode;
        var isFish = mode == "fish";

        SetBrush("MainBackgroundBrush", "#FFFFFF");
        SetSidebarBrush(isFish);
        SetBrush("CardBrush", isFish ? "#F8F9F9" : "#F8FBFD");
        SetBrush("BookCardBrush", "#E9F3FD");
        SetBrush("PrimaryTextBrush", isFish ? "#424A4E" : "#3F4B52");
        SetBrush("SecondaryTextBrush", isFish ? "#838C91" : "#81909A");
        SetBrush("FaintTextBrush", isFish ? "#ADB4B8" : "#AAB6BD");
        SetBrush("LineBrush", isFish ? "#E7E9EA" : "#E5EEF3");
        SetBrush("AccentBrush", isFish ? "#C6CDD1" : "#B9DDF0");
        SetBrush("AccentDarkBrush", isFish ? "#59676E" : "#4E7185");
        SetBrush("AccentSoftBrush", isFish ? "#F2F4F5" : "#F2F8FC");
        SetBrush("SidebarHoverBrush", isFish ? "#3EFFFFFF" : "#48FFFFFF");
        SetBrush("SidebarSelectedBrush", "#FFFFFF");
        SetBrush("SidebarSelectedLabelBrush", _sidebarExpanded ? "#C6F7BD" : "#00C6F7BD");
        System.Windows.Application.Current.Resources["SidebarSelectedMargin"] =
            _sidebarExpanded ? new Thickness(0, 0, 20, 0) : new Thickness(0);
        SetBrush("SelectedTextBrush", isFish ? "#56656C" : "#356A88");
        SetBrush("SidebarTextBrush", isFish ? "#68777E" : "#55798E");
        SetBrush("SidebarActiveTextBrush", isFish ? "#56656C" : "#356A88");
        SetBrush("SidebarLogoBrush", isFish ? "#26FFFFFF" : "#2AFFFFFF");
        SetBrush("SidebarEdgeBrush", isFish ? "#3EFFFFFF" : "#48FFFFFF");
        SetBrush("ModeBrush", isFish ? "#D8FFFFFF" : "#DFFFFFFF");
        SetBrush("ControlSurfaceBrush", isFish ? "#F3F5F5" : "#F2F7FA");
        SetBrush("ControlHoverBrush", isFish ? "#E8ECEE" : "#E5F1F7");
        SetBrush("LimeBrush", isFish ? "#D0D9BF" : "#C4EAB5");
        SetBrush("LimeSoftBrush", isFish ? "#F5F7F0" : "#F3FAF0");
        SetBrush("LimeDarkBrush", isFish ? "#6E775F" : "#64805E");

        SeaSLogo.Visibility = isFish ? Visibility.Collapsed : Visibility.Visible;
        LittleFishLogo.Visibility = isFish ? Visibility.Visible : Visibility.Collapsed;
        ModeToggleButton.ToolTip = isFish ? "切换到休闲模式" : "切换到摸鱼模式";
        ModeToggleButton.Tag = isFish ? "休闲模式" : "摸鱼模式";

        if (save)
        {
            SaveState();
            ShowToast(isFish ? "已切换到摸鱼模式" : "已切换到休闲模式");
        }
    }

    private static void SetBrush(string resourceKey, string color)
    {
        System.Windows.Application.Current.Resources[resourceKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static void SetSidebarBrush(bool isFish)
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        gradient.GradientStops.Add(new GradientStop(
            (Color)ColorConverter.ConvertFromString(isFish ? "#CBD3D7" : "#AFD0EC"), 0));
        gradient.GradientStops.Add(new GradientStop(
            (Color)ColorConverter.ConvertFromString(isFish ? "#D6DCE0" : "#B8D6EE"), 0.55));
        gradient.GradientStops.Add(new GradientStop(
            (Color)ColorConverter.ConvertFromString(isFish ? "#E1E6E8" : "#C3DCF1"), 1));
        System.Windows.Application.Current.Resources["SidebarBrush"] = gradient;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "设置页会在主页完成后接入。", "SeaS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateWindowStateVisuals();
        ScheduleWindowPlacementSave();
    }

    private void UpdateWindowStateVisuals()
    {
        if (MaximizeWindowIcon is not null)
        {
            MaximizeWindowIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        if (WindowBorder is not null)
        {
            WindowBorder.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(10);
            WindowBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        }

        if (SidebarBorder is not null)
        {
            SidebarBorder.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(10, 0, 0, 10);
        }
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            TryDragWindow();
            e.Handled = true;
        }
    }

    private void WindowBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReaderHost.Visibility == Visibility.Visible || e.GetPosition(this).Y > 74 || IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            TryDragWindow();
            e.Handled = true;
        }
    }

    private void TryDragWindow()
    {
        if (WindowState == WindowState.Maximized)
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

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBox)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
            {
                return target;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject source)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
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

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (ReaderHost.Visibility == Visibility.Visible && !(Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W))
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            ImportFiles_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Clear();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            Close();
            e.Handled = true;
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void SaveState()
    {
        try
        {
            LibraryStore.Save(_state);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"保存书架数据失败：{exception.Message}", "SeaS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestoreWindowPlacement()
    {
        _state.WindowPreferences ??= new WindowPreferences();
        var placement = _state.WindowPreferences;
        if (!placement.HasPlacement)
        {
            return;
        }

        var width = Math.Max(MinWidth, placement.Width);
        var height = Math.Max(MinHeight, placement.Height);
        var windowBounds = new Rect(placement.Left, placement.Top, width, height);
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        if (!windowBounds.IntersectsWith(virtualScreen))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = width;
        Height = height;
        if (placement.IsMaximized)
        {
            Loaded += (_, _) => WindowState = WindowState.Maximized;
        }
    }

    private void ScheduleWindowPlacementSave()
    {
        if (!_windowPlacementReady || _isHiddenToTray)
        {
            return;
        }

        _windowPlacementTimer.Stop();
        _windowPlacementTimer.Start();
    }

    private void SaveWindowPlacement()
    {
        if (!_windowPlacementReady)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.Width < MinWidth || bounds.Height < MinHeight)
        {
            return;
        }

        _state.WindowPreferences = new WindowPreferences
        {
            HasPlacement = true,
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized
        };
        SaveState();
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowPlacementTimer.Stop();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowPlacement();
        if (!_exitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }
}
