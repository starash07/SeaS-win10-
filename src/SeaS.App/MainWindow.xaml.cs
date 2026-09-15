using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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
    private const string BookDragDataFormat = "SeaS.BookItem";
    private static readonly string[] SupportedBookExtensions = [".txt", ".epub", ".md", ".markdown"];

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

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
    private BookItem? _bookDragCandidate;
    private Point _bookDragStartPoint;
    private bool _ignoreBookClickAfterDrag;
    private bool _isBookDragging;
    private Popup? _bookDragPopup;
    private Forms.NotifyIcon? _trayIcon;
    private TrayMenuWindow? _trayMenuWindow;
    private Process? _hostedLittleFishProcess;
    private string? _hostedLittleFishPipeName;
    private bool _isClosingHostedLittleFishForExit;
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
        InitializeTrayIcon();
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
        Dispatcher.BeginInvoke(() => UpdateTopTabSelectionSlider(animate: false), DispatcherPriority.Loaded);
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
            EmptyStateHint.Text = "拖入 TXT / EPUB / Markdown 文件，或从左侧导入";
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

        if (_state.Bookmarks is null)
        {
            _state.Bookmarks = [];
            changed = true;
        }

        if (_state.ReaderPreferences is null)
        {
            _state.ReaderPreferences = new ReaderPreferences();
            changed = true;
        }

        if (_state.ShortcutPreferences is null)
        {
            _state.ShortcutPreferences = new ReaderShortcutPreferences();
            changed = true;
        }

        if (_state.ShortcutPreferences.Normalize())
        {
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

        UpdateTopTabSelectionSlider(animate: true);
    }

    private void UpdateTopTabSelectionSlider(bool animate)
    {
        if (ShelfTabsHost.Visibility != Visibility.Visible)
        {
            return;
        }

        var selected = new[] { AllBooksTab, GroupedBooksTab, FavoriteBooksTab }
            .FirstOrDefault(tab => tab.IsChecked == true);

        if (selected is null)
        {
            return;
        }

        if (!IsLoaded || selected.ActualWidth <= 0 || ShelfTabsHost.ActualWidth <= 0)
        {
            Dispatcher.BeginInvoke(() => UpdateTopTabSelectionSlider(animate: false), DispatcherPriority.Loaded);
            return;
        }

        var targetX = selected.TransformToAncestor(ShelfTabsHost).Transform(new Point(0, 0)).X;
        var targetWidth = selected.ActualWidth;

        if (!animate)
        {
            TopTabSelectionSlider.BeginAnimation(FrameworkElement.WidthProperty, null);
            TopTabSelectionSliderTransform.BeginAnimation(TranslateTransform.XProperty, null);
            TopTabSelectionSlider.Width = targetWidth;
            TopTabSelectionSliderTransform.X = targetX;
            return;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        TopTabSelectionSlider.BeginAnimation(
            FrameworkElement.WidthProperty,
            new DoubleAnimation(TopTabSelectionSlider.ActualWidth > 0 ? TopTabSelectionSlider.ActualWidth : TopTabSelectionSlider.Width, targetWidth, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = easing
            },
            HandoffBehavior.SnapshotAndReplace);
        TopTabSelectionSliderTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(TopTabSelectionSliderTransform.X, targetX, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = easing
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateTopNavigationVisuals()
    {
        var isRecent = _activeFilter == "recent";
        var isGrouped = _activeFilter == "groups";
        ShelfTabsHost.Visibility = isRecent ? Visibility.Collapsed : Visibility.Visible;
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

        var result = SeaSMessageBox.Show(
            this,
            $"从书架移除选中的 {selectedBooks.Count} 本书？\n原文件不会被删除。",
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
        var missingBooks = GetMissingBooks(refreshViews: true);
        if (missingBooks.Count == 0)
        {
            ShowToast("没有发现失效书籍");
            return;
        }

        ShowMissingBooksCleanupDialog(missingBooks);
    }

    private List<BookItem> GetMissingBooks(bool refreshViews)
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

        if (refreshViews)
        {
            RefreshViews();
        }

        return missingBooks;
    }

    private bool ShowMissingBooksCleanupDialog(IEnumerable<BookItem>? knownMissingBooks = null)
    {
        var missingBooks = knownMissingBooks?.ToList() ?? GetMissingBooks(refreshViews: true);
        if (missingBooks.Count == 0)
        {
            ShowToast("没有发现失效书籍");
            return false;
        }

        var dialog = new MissingBooksWindow(missingBooks)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedBooks.Count == 0)
        {
            return false;
        }

        foreach (var book in dialog.SelectedBooks)
        {
            _state.Books.Remove(book);
        }

        SaveState();
        RefreshViews();
        ShowToast($"已清理 {dialog.SelectedBooks.Count} 本失效书籍");
        return true;
    }

    private bool HandleMissingBook(BookItem book)
    {
        RefreshViews();
        var dialog = new MissingBookActionWindow(book.Title)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        switch (dialog.SelectedAction)
        {
            case MissingBookAction.Relocate:
                return RelocateBookFile(book);
            case MissingBookAction.CleanMissing:
                ShowMissingBooksCleanupDialog();
                return false;
            default:
                return false;
        }
    }

    private bool RelocateBookFile(BookItem book)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"定位《{book.Title}》的新文件",
            Filter = "文本与电子书 (*.txt;*.epub;*.md;*.markdown)|*.txt;*.epub;*.md;*.markdown|TXT 文本文件 (*.txt)|*.txt|EPUB 电子书 (*.epub)|*.epub|Markdown 文件 (*.md;*.markdown)|*.md;*.markdown",
            Multiselect = false,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(dialog.FileName);
        if (!IsSupportedBookFile(fullPath))
        {
            SeaSMessageBox.Show(this, "请选择 TXT、EPUB 或 Markdown 文件。", "SeaS", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var duplicate = _state.Books.FirstOrDefault(item =>
            !ReferenceEquals(item, book)
            && string.Equals(Path.GetFullPath(item.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            SeaSMessageBox.Show(this, $"这个文件已经在书架中：\n《{duplicate.Title}》", "SeaS", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            book.FilePath = fullPath;
            book.FileSize = fileInfo.Length;
            book.RefreshFileState();
            SaveState();
            RefreshViews();
            ShowToast("已重新定位原文件");
            return true;
        }
        catch (Exception exception)
        {
            SeaSMessageBox.Show(this, $"重新定位失败：{exception.Message}", "SeaS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入书籍文件",
            Filter = "文本与电子书 (*.txt;*.epub;*.md;*.markdown)|*.txt;*.epub;*.md;*.markdown|TXT 文本文件 (*.txt)|*.txt|EPUB 电子书 (*.epub)|*.epub|Markdown 文件 (*.md;*.markdown)|*.md;*.markdown",
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
            Title = "选择包含书籍文件的文件夹",
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
            if (File.Exists(path) && IsSupportedBookFile(path))
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
                files.AddRange(Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(IsSupportedBookFile));
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
            var (title, author, coverImagePath) = ReadImportedBookMetadata(fullPath);
            _state.Books.Add(new BookItem
            {
                Title = title,
                Author = author,
                FilePath = fullPath,
                CoverImagePath = coverImagePath,
                FileSize = fileInfo.Length,
                AddedAt = DateTime.Now
            });
            addedCount++;
        }

        SaveState();
        RefreshViews();
        ShowToast(addedCount > 0 ? $"已导入 {addedCount} 本书" : "没有发现新的书籍文件");
    }

    private void Window_PreviewDragOver(object sender, WpfDragEventArgs e)
    {
        if (_activeFilter == "groups" && e.Data.GetDataPresent(BookDragDataFormat))
        {
            e.Effects = DragDropEffects.Copy;
            return;
        }

        e.Effects = HasImportableDropData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, WpfDragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            if (paths.Any(IsImportablePath))
            {
                ImportPaths(paths);
            }
            else
            {
                ShowToast("请拖入 TXT、EPUB、Markdown 文件，或包含这些文件的文件夹");
            }

            e.Handled = true;
        }
    }

    private static bool HasImportableDropData(IDataObject data)
    {
        return data.GetDataPresent(DataFormats.FileDrop)
            && data.GetData(DataFormats.FileDrop) is string[] paths
            && paths.Any(IsImportablePath);
    }

    private static bool IsImportablePath(string path)
    {
        return (File.Exists(path) && IsSupportedBookFile(path))
            || Directory.Exists(path);
    }

    private static bool IsSupportedBookFile(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedBookExtensions.Any(supported =>
            string.Equals(extension, supported, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTxtBook(BookItem book)
    {
        return string.Equals(Path.GetExtension(book.FilePath), ".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMarkdownBook(BookItem book)
    {
        var extension = Path.GetExtension(book.FilePath);
        return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEpubBook(BookItem book)
    {
        return string.Equals(Path.GetExtension(book.FilePath), ".epub", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SupportsLittleFish(BookItem book)
    {
        return IsTxtBook(book);
    }

    private static string GetBookFormatName(BookItem book)
    {
        if (IsEpubBook(book))
        {
            return "EPUB";
        }

        if (IsMarkdownBook(book))
        {
            return "Markdown";
        }

        return string.IsNullOrWhiteSpace(book.Extension) ? "该" : book.Extension;
    }

    private (string Title, string Author, string CoverImagePath) ReadImportedBookMetadata(string fullPath)
    {
        if (string.Equals(Path.GetExtension(fullPath), ".epub", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var metadata = EpubBookReader.ReadMetadata(fullPath);
                var coverImagePath = SaveImportedEpubCover(fullPath, metadata.CoverImageBytes);
                return (metadata.Title, metadata.Author, coverImagePath);
            }
            catch
            {
            }
        }

        var (title, author) = BookMetadata.FromFileName(Path.GetFileNameWithoutExtension(fullPath));
        return (title, author, string.Empty);
    }

    private static string SaveImportedEpubCover(string sourcePath, byte[]? coverImageBytes)
    {
        if (coverImageBytes is null || coverImageBytes.Length == 0)
        {
            return string.Empty;
        }

        Directory.CreateDirectory(LibraryStore.CoverDirectory);
        var extension = DetectImageExtension(coverImageBytes);
        var coverPath = Path.Combine(
            LibraryStore.CoverDirectory,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(coverPath, coverImageBytes);
        return coverPath;
    }

    private static string DetectImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 4
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47)
        {
            return ".png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return ".gif";
        }

        return ".img";
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
        if (_ignoreBookClickAfterDrag)
        {
            e.Handled = true;
            return;
        }

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

    private void BookCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _bookDragCandidate = null;

        if (_activeFilter != "groups" || IsBulkMode)
        {
            return;
        }

        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is { } sourceButton
            && !ReferenceEquals(sourceButton, sender))
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is not BookItem book)
        {
            return;
        }

        _bookDragCandidate = book;
        _bookDragStartPoint = e.GetPosition(null);
    }

    private void BookCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_bookDragCandidate is null || e.LeftButton != MouseButtonState.Pressed || _activeFilter != "groups")
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _bookDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPosition.Y - _bookDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (sender is not FrameworkElement dragElement)
        {
            _bookDragCandidate = null;
            return;
        }

        var draggedBook = _bookDragCandidate;
        var data = new DataObject(BookDragDataFormat, draggedBook);
        _isBookDragging = true;
        ShowBookDragPopup(dragElement);
        AnimateBookDragFeedback(dragElement, isDragging: true);
        UpdateBookDragPopupPosition();

        try
        {
            DragDrop.DoDragDrop(dragElement, data, DragDropEffects.Copy);
        }
        finally
        {
            _isBookDragging = false;
            CloseBookDragPopup();
            AnimateBookDragFeedback(dragElement, isDragging: false);
            ClearGroupDragOverState();
            _bookDragCandidate = null;
            _ignoreBookClickAfterDrag = true;
            Dispatcher.BeginInvoke(
                new Action(() => _ignoreBookClickAfterDrag = false),
                DispatcherPriority.Background);
        }

        e.Handled = true;
    }

    private void BookCard_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (!_isBookDragging)
        {
            return;
        }

        UpdateBookDragPopupPosition();
        e.UseDefaultCursors = true;
    }

    private void ShowBookDragPopup(FrameworkElement dragElement)
    {
        CloseBookDragPopup();

        var snapshotSource = dragElement;
        if (dragElement is ContentControl { Content: FrameworkElement contentElement }
            && contentElement.ActualWidth > 0
            && contentElement.ActualHeight > 0)
        {
            snapshotSource = contentElement;
        }

        var snapshot = CreateDragSnapshot(snapshotSource);
        if (snapshot is null)
        {
            return;
        }

        var preview = new Image
        {
            Width = snapshotSource.ActualWidth,
            Height = snapshotSource.ActualHeight,
            Source = snapshot,
            Opacity = 0.6,
            IsHitTestVisible = false,
            Stretch = Stretch.Fill
        };

        var root = new Border
        {
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Child = preview,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 6,
                Direction = 270,
                Color = Color.FromRgb(106, 135, 151),
                Opacity = 0.18
            }
        };

        _bookDragPopup = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.AbsolutePoint,
            StaysOpen = true,
            Child = root
        };

        _bookDragPopup.IsOpen = true;
        AnimateBookDragPopupPickup(root, preview);
    }

    private static void AnimateBookDragPopupPickup(FrameworkElement root, UIElement preview)
    {
        if (root.RenderTransform is not ScaleTransform scale)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(115);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.88, duration)
            {
                EasingFunction = easing
            },
            HandoffBehavior.SnapshotAndReplace);

        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.88, duration)
            {
                EasingFunction = easing
            },
            HandoffBehavior.SnapshotAndReplace);

        preview.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0.82, duration)
            {
                EasingFunction = easing
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private RenderTargetBitmap? CreateDragSnapshot(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return null;
        }

        var transform = PresentationSource.FromVisual(element)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var dpiX = 96 * transform.M11;
        var dpiY = 96 * transform.M22;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * transform.M11));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * transform.M22));

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpiX, dpiY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    private void UpdateBookDragPopupPosition()
    {
        if (_bookDragPopup is null || !_bookDragPopup.IsOpen || !GetCursorPos(out var cursorPoint))
        {
            return;
        }

        var position = new Point(cursorPoint.X, cursorPoint.Y);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            position = target.TransformFromDevice.Transform(position);
        }

        _bookDragPopup.HorizontalOffset = position.X + 12;
        _bookDragPopup.VerticalOffset = position.Y + 10;
    }

    private void CloseBookDragPopup()
    {
        if (_bookDragPopup is null)
        {
            return;
        }

        _bookDragPopup.IsOpen = false;
        _bookDragPopup.Child = null;
        _bookDragPopup = null;
    }

    private void GroupSection_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (_activeFilter == "groups" && e.Data.GetDataPresent(BookDragDataFormat))
        {
            SetGroupDragOverState((sender as FrameworkElement)?.DataContext as GroupSectionViewModel);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void GroupSection_DragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupSectionViewModel section)
        {
            section.IsDragOver = false;
        }
    }

    private void GroupSection_Drop(object sender, DragEventArgs e)
    {
        ClearGroupDragOverState();

        if (_activeFilter != "groups"
            || !e.Data.GetDataPresent(BookDragDataFormat)
            || e.Data.GetData(BookDragDataFormat) is not BookItem book
            || (sender as FrameworkElement)?.DataContext is not GroupSectionViewModel section)
        {
            return;
        }

        var targetGroupId = section.IsUngrouped ? null : section.GroupId;
        if (book.GroupId == targetGroupId)
        {
            return;
        }

        book.GroupId = targetGroupId;
        SaveState();
        RefreshViews();
        ShowToast(targetGroupId is null ? "已移动到未分组" : $"已移动到“{section.Name}”");
        e.Handled = true;
    }

    private static void AnimateBookDragFeedback(FrameworkElement element, bool isDragging)
    {
        if (element.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        var duration = TimeSpan.FromMilliseconds(isDragging ? 120 : 150);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var targetOpacity = isDragging ? 0.36 : 1;
        var targetScale = isDragging ? 0.975 : 1;

        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(targetOpacity, duration)
            {
                EasingFunction = easing
            },
            HandoffBehavior.SnapshotAndReplace);

        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(targetScale, duration)
            {
                EasingFunction = easing
            },
            HandoffBehavior.SnapshotAndReplace);

        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(targetScale, duration)
            {
                EasingFunction = easing
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void SetGroupDragOverState(GroupSectionViewModel? targetSection)
    {
        foreach (var section in GroupSections)
        {
            section.IsDragOver = ReferenceEquals(section, targetSection);
        }
    }

    private void ClearGroupDragOverState()
    {
        foreach (var section in GroupSections)
        {
            section.IsDragOver = false;
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
            if (HandleMissingBook(book))
            {
                OpenBook(book, useLittleFish);
            }

            return;
        }

        if (useLittleFish && !SupportsLittleFish(book))
        {
            var result = SeaSMessageBox.Show(
                this,
                $"{GetBookFormatName(book)} 格式暂不支持摸鱼模式。\n是否使用普通阅读模式打开？",
                "SeaS",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information,
                showCloseButton: false,
                distributeButtons: true);
            if (result != MessageBoxResult.OK)
            {
                return;
            }

            useLittleFish = false;
        }

        if (useLittleFish)
        {
            var executablePath = FindBundledLittleFishExecutable();
            if (executablePath is null)
            {
                SeaSMessageBox.Show(this, "SeaS 内置的 LittleFish 文件缺失，请重新安装或修复 SeaS。", "SeaS", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var pipeName = CreateHostedLittleFishPipeName();
                var startInfo = new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
                };
                startInfo.ArgumentList.Add("--seas-hosted");
                startInfo.ArgumentList.Add("--seas-host-pipe");
                startInfo.ArgumentList.Add(pipeName);
                startInfo.ArgumentList.Add(book.FilePath);
                var process = Process.Start(startInfo);
                if (process is null)
                {
                    throw new InvalidOperationException("LittleFish 进程未能启动。");
                }

                TrackHostedLittleFish(process, pipeName);
                MarkOpened(book);
                HideToTray();
            }
            catch (Exception exception)
            {
                SeaSMessageBox.Show(this, $"使用 LittleFish 打开失败：{exception.Message}", "SeaS", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            SeaSMessageBox.Show(this, $"打开阅读器失败：{exception.Message}", "SeaS", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private static string CreateHostedLittleFishPipeName()
    {
        return $"SeaS.LittleFish.Host.{Environment.ProcessId}.{Guid.NewGuid():N}";
    }

    private void TrackHostedLittleFish(Process process, string pipeName)
    {
        ClearHostedLittleFishTracking();
        _hostedLittleFishProcess = process;
        _hostedLittleFishPipeName = pipeName;

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += HostedLittleFish_Exited;
        }
        catch (InvalidOperationException)
        {
            ClearHostedLittleFishTracking();
        }
    }

    private void HostedLittleFish_Exited(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_hostedLittleFishProcess, sender))
            {
                return;
            }

            ClearHostedLittleFishTracking();
            if (!_exitRequested && !_isClosingHostedLittleFishForExit)
            {
                RestoreFromTray();
            }
        }));
    }

    private void ClearHostedLittleFishTracking()
    {
        if (_hostedLittleFishProcess is not null)
        {
            _hostedLittleFishProcess.Exited -= HostedLittleFish_Exited;
            _hostedLittleFishProcess.Dispose();
        }

        _hostedLittleFishProcess = null;
        _hostedLittleFishPipeName = null;
    }

    private bool HasActiveHostedLittleFish()
    {
        if (_hostedLittleFishProcess is null)
        {
            return false;
        }

        try
        {
            if (!_hostedLittleFishProcess.HasExited)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }

        ClearHostedLittleFishTracking();
        return false;
    }

    private bool SendHostedLittleFishCommand(string command)
    {
        if (!HasActiveHostedLittleFish() || string.IsNullOrWhiteSpace(_hostedLittleFishPipeName))
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", _hostedLittleFishPipeName, PipeDirection.Out);
            client.Connect(350);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch (TimeoutException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private bool TryShowHostedLittleFish()
    {
        return SendHostedLittleFishCommand("SHOW");
    }

    private void CloseHostedLittleFishForExit()
    {
        if (!HasActiveHostedLittleFish())
        {
            return;
        }

        _isClosingHostedLittleFishForExit = true;
        if (SendHostedLittleFishCommand("CLOSE"))
        {
            return;
        }

        try
        {
            _hostedLittleFishProcess?.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void InitializeTrayIcon()
    {
        var executablePath = Environment.ProcessPath;
        var icon = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath)
            ? System.Drawing.Icon.ExtractAssociatedIcon(executablePath)
            : null;

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "SeaS 阅读器",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(HandleTrayDoubleClick));
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
            {
                Dispatcher.BeginInvoke(new Action(ShowTrayMenu));
            }
        };
    }

    private void HandleTrayDoubleClick()
    {
        if (TryShowHostedLittleFish())
        {
            return;
        }

        RestoreFromTray();
    }

    private void ImportFilesFromTray()
    {
        RestoreFromTray();
        ImportFiles_Click(this, new RoutedEventArgs());
    }

    private void ScanFolderFromTray()
    {
        RestoreFromTray();
        ScanFolder_Click(this, new RoutedEventArgs());
    }

    private void ToggleModeFromTray()
    {
        SetMode(_state.Mode == "fish" ? "leisure" : "fish", save: true);
        if (IsVisible && !_isHiddenToTray)
        {
            ShowToast(_state.Mode == "fish" ? "已切换到摸鱼模式" : "已切换到休闲模式");
        }
    }

    private void ShowTrayMenu()
    {
        _trayMenuWindow?.Close();

        var modeActionText = _state.Mode == "fish" ? "切换到休闲模式" : "切换到摸鱼模式";
        var menu = new TrayMenuWindow(
            modeActionText,
            RestoreFromTray,
            ImportFilesFromTray,
            ScanFolderFromTray,
            ToggleModeFromTray,
            ExitFromTray);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayMenuWindow, menu))
            {
                _trayMenuWindow = null;
            }
        };

        _trayMenuWindow = menu;
        menu.ShowNearCursor();
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

    private void FavoriteStar_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not BookItem book)
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
            HandleMissingBook(book);
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

        var result = SeaSMessageBox.Show(this, $"从书架移除《{book.Title}》？\n原文件不会被删除。", "SeaS", MessageBoxButton.OKCancel, MessageBoxImage.Question);
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
        UpdateSidebarSelectedVisuals();
        System.Windows.Application.Current.Resources["SidebarSelectedMargin"] = new Thickness(0);
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
        UpdateSidebarSelectedVisuals();
        System.Windows.Application.Current.Resources["SidebarSelectedMargin"] = new Thickness(0);
        AnimateSidebarControls(36, GetCollapsedModeButtonSize(), 160, EasingMode.EaseInOut);
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

        AnimateWidth(HelpButton, navigationWidth, durationMs, easingMode);
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

    private double GetCollapsedModeButtonSize()
    {
        return WindowState == WindowState.Maximized ? 51 : 57;
    }

    private void SetMode(string mode, bool save)
    {
        _state.Mode = mode;
        var isFish = mode == "fish";

        SetBrush("MainBackgroundBrush", "#FFFFFF");
        SetSidebarBrush(isFish);
        SetBrush("CardBrush", isFish ? "#FFFFFF" : "#F8FBFD");
        SetBrush("BookCardBrush", isFish ? "#DDEFF7" : "#E9F3FD");
        SetBrush("BookCardBrushCream", isFish ? "#FFF1D8" : "#E9F3FD");
        SetBrush("BookCardBrushRose", isFish ? "#F7E2DF" : "#E9F3FD");
        SetBrush("BookCardBrushMint", isFish ? "#E6F3EA" : "#E9F3FD");
        SetBrush("BookCardBorderBrush", "#00FFFFFF");
        SetBrush("BookCardChipBrush", isFish ? "#F2FAFD" : "#F2F8FC");
        SetBrush("LibraryToolsBackgroundBrush", "#00FFFFFF");
        SetBrush("PrimaryTextBrush", isFish ? "#2F4149" : "#3F4B52");
        SetBrush("SecondaryTextBrush", isFish ? "#6D8793" : "#81909A");
        SetBrush("FaintTextBrush", isFish ? "#A6BBC5" : "#AAB6BD");
        SetBrush("LineBrush", isFish ? "#E5F0F5" : "#E5EEF3");
        SetBrush("AccentBrush", isFish ? "#7DB9D6" : "#B9DDF0");
        SetBrush("AccentDarkBrush", isFish ? "#3C7894" : "#4E7185");
        SetBrush("AccentSoftBrush", isFish ? "#F2FAFD" : "#F2F8FC");
        SetBrush("TopTabIdleBrush", isFish ? "#E2F0F3" : "#00FFFFFF");
        SetBrush("TopTabSelectedBrush", isFish ? "#6AAACA" : "#F2F8FC");
        SetBrush("TopTabHoverBrush", isFish ? "#D7EBF1" : "#F2F8FC");
        SetBrush("TopTabTextBrush", isFish ? "#314047" : "#81909A");
        SetBrush("TopTabSelectedTextBrush", isFish ? "#FFFFFF" : "#4E7185");
        SetBrush("SidebarHoverBrush", isFish ? "#6AFFFFFF" : "#48FFFFFF");
        UpdateSidebarSelectedVisuals();
        System.Windows.Application.Current.Resources["SidebarSelectedMargin"] =
            new Thickness(0);
        SetBrush("SelectedTextBrush", isFish ? "#2F7EA0" : "#356A88");
        SetBrush("SidebarTextBrush", isFish ? "#527E92" : "#55798E");
        SetBrush("SidebarActiveTextBrush", isFish ? "#2F7EA0" : "#356A88");
        SetBrush("SidebarIconBrush", isFish ? "#527E92" : "#B3D1EB");
        SetBrush("SidebarActiveIconBrush", isFish ? "#FFFFFF" : "#E8B8B8");
        SetBrush("SidebarLogoBrush", isFish ? "#36FFFFFF" : "#2AFFFFFF");
        SetBrush("SidebarEdgeBrush", isFish ? "#7AFFFFFF" : "#48FFFFFF");
        SetBrush("ModeBrush", isFish ? "#F6E3DF" : "#DFFFFFFF");
        SetBrush("ModeIconBrush", isFish ? "#DD9184" : "#356A88");
        SetBrush("ControlSurfaceBrush", isFish ? "#F2FAFD" : "#F2F7FA");
        SetBrush("ControlHoverBrush", isFish ? "#E9F6FB" : "#E5F1F7");
        SetBrush("LimeBrush", isFish ? "#E6F3EA" : "#C4EAB5");
        SetBrush("LimeSoftBrush", isFish ? "#F7FFF9" : "#F3FAF0");
        SetBrush("LimeDarkBrush", isFish ? "#5E8A70" : "#64805E");
        SetBrush("FavoriteStarBrush", isFish ? "#D8A84E" : "#E5A93E");
        SetBrush("WarmAccentBrush", isFish ? "#D98379" : "#AA6A2A");
        System.Windows.Application.Current.Resources["BookCardCornerRadius"] =
            isFish ? new CornerRadius(16) : new CornerRadius(6);
        RefreshBookCardVisuals();

        SeaSLogo.Visibility = isFish ? Visibility.Collapsed : Visibility.Visible;
        LittleFishLogo.Visibility = isFish ? Visibility.Visible : Visibility.Collapsed;
        ModeToggleButton.ToolTip = isFish ? "当前为摸鱼模式，点击切换到休闲模式" : "当前为休闲模式，点击切换到摸鱼模式";
        ModeToggleButton.Tag = isFish ? "摸鱼模式" : "休闲模式";

        if (save)
        {
            SaveState();
            ShowToast(isFish ? "已切换到摸鱼模式" : "已切换到休闲模式");
        }
    }

    private void RefreshBookCardVisuals()
    {
        ShelfItemsControl?.Items.Refresh();
        GroupSectionsItemsControl?.Items.Refresh();
    }

    private void UpdateSidebarSelectedVisuals()
    {
        var isFish = _state.Mode == "fish";
        SetBrush("SidebarSelectedItemBrush", isFish ? "#00FFFFFF" : "#FFFFFF");
        SetBrush("SidebarSelectedBrush", isFish
            ? "#6CA8CA"
            : "#00FFFFFF");
        SetBrush("SidebarSelectedLabelBrush", "#00FFFFFF");
    }

    private static void SetBrush(string resourceKey, string color)
    {
        System.Windows.Application.Current.Resources[resourceKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static void SetSidebarBrush(bool isFish)
    {
        if (isFish)
        {
            System.Windows.Application.Current.Resources["SidebarBrush"] =
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
            return;
        }

        System.Windows.Application.Current.Resources["SidebarBrush"] =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E9F3FD"));
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        _state.ShortcutPreferences ??= new ReaderShortcutPreferences();
        _state.ShortcutPreferences.Normalize();

        var dialog = new SettingsWindow(_state.ShortcutPreferences)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _state.ShortcutPreferences = dialog.ShortcutPreferences.Clone();
        _state.ShortcutPreferences.Normalize();
        SaveState();
        ShowToast("设置已保存");
    }

    private void OpenHelp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HelpWindow
        {
            Owner = this
        };
        dialog.ShowDialog();
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

        if (MaximizeWindowButton is not null)
        {
            MaximizeWindowButton.ToolTip = WindowState == WindowState.Maximized ? "恢复" : "最大化";
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

        if (SidebarBackground is not null)
        {
            SidebarBackground.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(10, 0, 0, 10);
        }

        if (SidebarContent is not null)
        {
            SidebarContent.Margin = WindowState == WindowState.Maximized ? new Thickness(6, 0, 0, 0) : new Thickness(0);
        }

        if (SidebarLogoHost is not null)
        {
            SidebarLogoHost.Margin = WindowState == WindowState.Maximized
                ? new Thickness(9, 14, 0, 0)
                : new Thickness(15, 14, 0, 0);
        }

        if (ModeToggleButton is not null)
        {
            var modeButtonSize = GetCollapsedModeButtonSize();
            ModeToggleButton.Height = modeButtonSize;
            if (!_sidebarExpanded)
            {
                ModeToggleButton.BeginAnimation(FrameworkElement.WidthProperty, null);
                ModeToggleButton.Width = modeButtonSize;
            }
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
            SeaSMessageBox.Show(this, $"保存书架数据失败：{exception.Message}", "SeaS", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        _trayMenuWindow?.Close();
        _trayMenuWindow = null;
        ClearHostedLittleFishTracking();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
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

        CloseHostedLittleFishForExit();
        base.OnClosing(e);
    }
}
