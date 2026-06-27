using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SeaS.App.Models;
using SeaS.App.Services;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SeaS.App;

public partial class EmbeddedReaderControl : UserControl
{
    private enum ReadingMode
    {
        Scroll,
        Page
    }

    private sealed record SearchMatch(
        int ChapterIndex,
        int ParagraphIndex,
        int CharacterIndex,
        string LocationText,
        string Preview);
    private sealed record PageAnchor(int ParagraphIndex, int CharacterIndex);
    private sealed record PageRunSegment(int StartCharacter, int Length, Run Run);

    private const double ReaderHorizontalInset = 68;
    private const double MinContentWidthPercent = 60;
    private const double MaxContentWidthPercent = 100;
    private const double ContentWidthPercentStep = 5;
    private const double FallbackContentWidth = 940;
    private const double ImmersiveHorizontalInset = 28;
    private const double ImmersiveHotZoneWidth = 50;
    private const int MaxSearchMatches = 2000;

    private readonly BookItem _book;
    private readonly LibraryState _state;
    private readonly Action _saveState;
    private readonly TxtBookDocument _document;
    private readonly List<ReaderBookmark> _currentBookmarks = [];
    private readonly List<SearchMatch> _searchMatches = [];
    private readonly DispatcherTimer _progressSaveTimer;
    private readonly DispatcherTimer _autoPageTimer;
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly DispatcherTimer _immersiveHideTimer;

    private int _chapterIndex;
    private double _fontSize = 18;
    private double _lineHeight = 1.95;
    private double _contentWidthPercent = 95;
    private FontFamily _readerFontFamily = new("Microsoft YaHei UI");
    private ReadingMode _readingMode = ReadingMode.Scroll;
    private string _theme = "white";
    private bool _immersiveMode;
    private bool _autoPageEnabled;
    private double _autoPageIntervalSeconds = 5;
    private readonly Dictionary<Paragraph, int> _pageParagraphIndices = [];
    private readonly Dictionary<int, List<PageRunSegment>> _pageRunSegments = [];
    private readonly DispatcherTimer _pageReflowTimer;
    private FlowDocument? _pageDocument;
    private DynamicDocumentPaginator? _pagePaginator;
    private int _pageIndex;
    private int _pageCount = 1;
    private PageAnchor? _pendingPageAnchor;
    private PageAnchor? _pendingRestorePageAnchor;
    private double? _pendingPageProgress;
    private object? _pagePaginationUserState;
    private bool _isPagePaginationPending;
    private bool _readerRailExpanded;
    private bool _isChromeVisible = true;
    private bool _isImmersiveRailVisible;
    private bool _suppressScrollProgress;
    private bool _isDraggingTotalProgress;
    private bool _isDraggingChapterProgress;
    private string _activeSearchKeyword = string.Empty;
    private int _searchIndex = -1;
    private bool _suppressSearchSelection;
    private Window? _hostWindow;

    public event EventHandler? BackRequested;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? CloseRequested;

    public EmbeddedReaderControl(BookItem book, LibraryState state, Action saveState)
    {
        InitializeComponent();

        _book = book;
        _state = state;
        _saveState = saveState;
        _document = TxtBookReader.Load(book);
        _pageReflowTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _pageReflowTimer.Tick += PageReflowTimer_Tick;
        _progressSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _progressSaveTimer.Tick += (_, _) =>
        {
            _progressSaveTimer.Stop();
            SaveCurrentReadingProgress();
        };
        _autoPageTimer = new DispatcherTimer(DispatcherPriority.Background);
        _autoPageTimer.Tick += AutoPageTimer_Tick;
        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            RefreshSearchResultsFromInput();
        };
        _immersiveHideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        _immersiveHideTimer.Tick += (_, _) =>
        {
            _immersiveHideTimer.Stop();
            if (_immersiveMode && !AnyPanelOpen())
            {
                HideImmersiveRail();
            }
        };

        BookTitleText.Text = _document.Title;
        BookAuthorText.Text = _document.Author;
        ChapterList.ItemsSource = _document.Chapters;

        LoadReaderPreferences();
        InitializeFontList();
        ApplyReadingMode(_readingMode, save: false);
        ApplyTheme(_theme, save: false);
        UpdateSettingText();
        UpdateAutoPageControls();
        UpdateImmersiveModeControls();
        SetChromeVisible(!_immersiveMode);
        RefreshBookmarks();
        NavigateToChapter(
            _book.LastReadChapterIndex,
            Math.Clamp(_book.LastReadChapterProgress, 0, 1));

        Loaded += ReaderControl_Loaded;
        Unloaded += ReaderControl_Unloaded;
    }

    private void LoadReaderPreferences()
    {
        _state.ReaderPreferences ??= new ReaderPreferences();
        var preferences = _state.ReaderPreferences;
        _fontSize = Math.Clamp(preferences.FontSize, 12, 32);
        _lineHeight = Math.Clamp(preferences.LineHeight, 1.2, 2.8);
        _contentWidthPercent = Math.Clamp(
            preferences.ContentWidthPercent,
            MinContentWidthPercent,
            MaxContentWidthPercent);
        _readerFontFamily = new FontFamily(
            string.IsNullOrWhiteSpace(preferences.FontFamily)
                ? "Microsoft YaHei UI"
                : preferences.FontFamily);
        _readingMode = string.Equals(preferences.ReadingMode, "page", StringComparison.OrdinalIgnoreCase)
            ? ReadingMode.Page
            : ReadingMode.Scroll;
        _theme = preferences.Theme is "eye" or "night" ? preferences.Theme : "white";
        _immersiveMode = preferences.ImmersiveMode;
        _autoPageEnabled = preferences.AutoPageEnabled;
        _autoPageIntervalSeconds = Math.Clamp(preferences.AutoPageIntervalSeconds, 1, 3600);
    }

    private void SaveReaderPreferences()
    {
        _state.ReaderPreferences = new ReaderPreferences
        {
            FontSize = _fontSize,
            LineHeight = _lineHeight,
            ContentWidthPercent = _contentWidthPercent,
            FontFamily = _readerFontFamily.Source,
            ReadingMode = _readingMode == ReadingMode.Page ? "page" : "scroll",
            Theme = _theme,
            ImmersiveMode = _immersiveMode,
            AutoPageEnabled = _autoPageEnabled,
            AutoPageIntervalSeconds = _autoPageIntervalSeconds
        };
        _saveState();
    }

    private void ReaderControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged -= HostWindow_StateChanged;
        }

        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged += HostWindow_StateChanged;
        }

        UpdateReaderClip();
        Focus();
        SetChromeVisible(!_immersiveMode);
        ConfigureAutoPageTimer();
        if (_readingMode == ReadingMode.Page)
        {
            Dispatcher.BeginInvoke(
                () => RebuildCurrentChapter(keepProgress: true),
                DispatcherPriority.Loaded);
        }
    }

    private void ReaderControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _pageReflowTimer.Stop();
        _progressSaveTimer.Stop();
        _autoPageTimer.Stop();
        _searchDebounceTimer.Stop();
        _immersiveHideTimer.Stop();
        SaveCurrentReadingProgress();
        DetachPagePaginator();

        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged -= HostWindow_StateChanged;
            _hostWindow = null;
        }
    }

    private void HostWindow_StateChanged(object? sender, EventArgs e) => UpdateReaderClip();

    private void ReaderRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateReaderClip();

    private void UpdateReaderClip()
    {
        if (ReaderRoot.ActualWidth <= 0 || ReaderRoot.ActualHeight <= 0)
        {
            return;
        }

        var radius = _hostWindow?.WindowState == WindowState.Maximized ? 0 : 10;
        ReaderRoot.Clip = new RectangleGeometry(
            new Rect(0, 0, ReaderRoot.ActualWidth, ReaderRoot.ActualHeight),
            radius,
            radius);
    }

    private void InitializeFontList()
    {
        var fonts = Fonts.SystemFontFamilies
            .OrderBy(font => font.Source, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        FontFamilyCombo.ItemsSource = fonts;
        var preferred = fonts.FirstOrDefault(font => string.Equals(font.Source, _readerFontFamily.Source, StringComparison.OrdinalIgnoreCase))
                        ?? fonts.FirstOrDefault(font => string.Equals(font.Source, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase))
                        ?? fonts.FirstOrDefault(font => string.Equals(font.Source, "Microsoft YaHei", StringComparison.OrdinalIgnoreCase))
                        ?? fonts.FirstOrDefault();

        if (preferred is not null)
        {
            _readerFontFamily = preferred;
            FontFamilyCombo.SelectionChanged -= FontFamilyCombo_SelectionChanged;
            FontFamilyCombo.SelectedItem = preferred;
            FontFamilyCombo.SelectionChanged += FontFamilyCombo_SelectionChanged;
        }
    }

    private void NavigateToChapter(
        int index,
        double chapterProgress = 0,
        int paragraphIndex = -1,
        int characterIndex = -1)
    {
        if (_document.ChapterCount == 0)
        {
            return;
        }

        _chapterIndex = Math.Clamp(index, 0, _document.ChapterCount - 1);
        var chapter = _document.Chapters[_chapterIndex];

        ChapterKickerText.Text = $"第 {chapter.Number:00} 章";
        HeaderChapterTitleText.Text = chapter.Title;

        ChapterList.SelectionChanged -= ChapterList_SelectionChanged;
        ChapterList.SelectedIndex = _chapterIndex;
        ChapterList.ScrollIntoView(chapter);
        ChapterList.SelectionChanged += ChapterList_SelectionChanged;

        RenderChapter(chapter, chapterProgress, paragraphIndex, characterIndex);

        Dispatcher.BeginInvoke(() =>
        {
            if (_readingMode == ReadingMode.Page)
            {
                UpdateProgress();
            }
            else if (paragraphIndex >= 0)
            {
                ScrollToParagraph(paragraphIndex);
            }
            else
            {
                ScrollToChapterProgress(chapterProgress);
            }

            UpdateProgress();
        }, DispatcherPriority.Loaded);
    }

    private void RenderChapter(
        ReaderChapter chapter,
        double chapterProgress = 0,
        int paragraphIndex = -1,
        int characterIndex = -1)
    {
        ApplyContentWidth();
        ArticlePanel.Children.Clear();
        ArticlePanel.Margin = _readingMode == ReadingMode.Page
            ? new Thickness(0, 34, 0, 34)
            : new Thickness(0, 44, 0, 86);

        if (_readingMode == ReadingMode.Page)
        {
            ReaderScrollViewer.Visibility = Visibility.Collapsed;
            ReaderPageView.Visibility = Visibility.Visible;
            BuildPagedChapter(chapter, chapterProgress, paragraphIndex, characterIndex);
            return;
        }

        ReaderPageView.Visibility = Visibility.Collapsed;
        DetachPagePaginator();
        ReaderScrollViewer.Visibility = Visibility.Visible;

        RenderHeading(chapter);

        if (chapter.Paragraphs.Count == 0)
        {
            AddEmptyParagraph();
            return;
        }

        for (var index = 0; index < chapter.Paragraphs.Count; index++)
        {
            ArticlePanel.Children.Add(CreateParagraphBlock(chapter.Paragraphs[index], index));
        }
    }

    private void RenderHeading(ReaderChapter chapter)
    {
        var badge = CreateChapterBadge(chapter);
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        ArticlePanel.Children.Add(badge);

        var titleBlock = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 34),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("PrimaryTextBrush", "#3F4B52"),
            Text = AddTitleSpacing(chapter.Title),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };

        if (GetCurrentSearchMatch() is { } match
            && match.ChapterIndex == _chapterIndex
            && match.ParagraphIndex < 0)
        {
            titleBlock.Background = GetBrush("LimeBrush", "#C4EAB5");
        }

        ArticlePanel.Children.Add(titleBlock);
    }

    private static string AddTitleSpacing(string title)
    {
        if (title.Length <= 1)
        {
            return title;
        }

        return string.Join('\u2009', title.ToCharArray());
    }

    private void BuildPagedChapter(
        ReaderChapter chapter,
        double chapterProgress,
        int paragraphIndex,
        int characterIndex)
    {
        DetachPagePaginator();
        _pageParagraphIndices.Clear();
        _pageRunSegments.Clear();
        _pendingRestorePageAnchor = null;
        _pendingPageProgress = null;

        var pageWidth = Math.Max(320, ReaderBody.ActualWidth);
        var pageHeight = Math.Max(320, ReaderBody.ActualHeight);
        var contentWidth = Math.Min(pageWidth, GetResponsiveContentWidth());
        var horizontalPadding = Math.Max(24, (pageWidth - contentWidth) / 2);

        _pageDocument = new FlowDocument
        {
            PageWidth = pageWidth,
            PageHeight = pageHeight,
            PagePadding = new Thickness(horizontalPadding, 34, horizontalPadding, 34),
            ColumnWidth = double.PositiveInfinity,
            ColumnGap = 0,
            FontFamily = _readerFontFamily,
            FontSize = _fontSize,
            Foreground = GetBrush("PrimaryTextBrush", "#3F4B52"),
            Background = GetBrush("ReaderBackgroundBrush", "#F7FAFC"),
            LineHeight = _fontSize * _lineHeight,
            TextAlignment = TextAlignment.Justify
        };

        AddPagedHeading(_pageDocument, chapter);
        if (chapter.Paragraphs.Count == 0)
        {
            _pageDocument.Blocks.Add(new Paragraph(new Run("这一章暂时没有正文内容。"))
            {
                Margin = new Thickness(0, 0, 0, 18),
                Foreground = GetBrush("SecondaryTextBrush", "#81909A"),
                TextAlignment = TextAlignment.Center
            });
        }
        else
        {
            for (var index = 0; index < chapter.Paragraphs.Count; index++)
            {
                _pageDocument.Blocks.Add(CreatePagedParagraph(chapter.Paragraphs[index], index));
            }
        }

        _pagePaginator = (DynamicDocumentPaginator)((IDocumentPaginatorSource)_pageDocument).DocumentPaginator;
        _pagePaginator.PageSize = new Size(pageWidth, pageHeight);
        _pagePaginator.PaginationProgress += PagePaginator_PaginationProgress;
        _pagePaginator.PaginationCompleted += PagePaginator_PaginationCompleted;
        _pagePaginationUserState = new object();
        _isPagePaginationPending = true;

        ReaderBody.Background = GetBrush("ReaderBackgroundBrush", "#F7FAFC");
        ReaderPageView.DocumentPaginator = _pagePaginator;
        ReaderPageView.PageNumber = 0;
        _pageIndex = 0;
        _pageCount = Math.Max(1, _pagePaginator.PageCount);

        if (paragraphIndex >= 0)
        {
            _pendingRestorePageAnchor = new PageAnchor(paragraphIndex, Math.Max(0, characterIndex));
        }
        else if (chapterProgress > 0)
        {
            _pendingPageProgress = Math.Clamp(chapterProgress, 0, 1);
        }

        _pagePaginator.ComputePageCountAsync(_pagePaginationUserState);
        UpdateProgress();
    }

    private void AddPagedHeading(FlowDocument document, ReaderChapter chapter)
    {
        var badge = CreateChapterBadge(chapter);

        document.Blocks.Add(new Paragraph(new InlineUIContainer(badge))
        {
            Margin = new Thickness(0),
            TextAlignment = TextAlignment.Center,
            KeepWithNext = true
        });

        var titleRun = new Run(AddTitleSpacing(chapter.Title));
        if (GetCurrentSearchMatch() is { } match
            && match.ChapterIndex == _chapterIndex
            && match.ParagraphIndex < 0)
        {
            titleRun.Background = GetBrush("LimeBrush", "#C4EAB5");
        }

        document.Blocks.Add(new Paragraph(titleRun)
        {
            Margin = new Thickness(0, 18, 0, 34),
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("PrimaryTextBrush", "#3F4B52"),
            TextAlignment = TextAlignment.Center,
            KeepWithNext = true
        });
    }

    private static Border CreateChapterBadge(ReaderChapter chapter)
    {
        var label = new TextBlock
        {
            Text = $"第 {chapter.Number:00} 章",
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("SimSun"),
            FontSize = 11,
            FontWeight = FontWeights.Normal,
            Foreground = GetBrush("LimeDarkBrush", "#557152"),
            LineHeight = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(label, TextRenderingMode.ClearType);

        return new Border
        {
            MinWidth = 64,
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            Background = GetBrush("LimeBrush", "#C6F7BD"),
            CornerRadius = new CornerRadius(13),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = label
        };
    }

    private Paragraph CreatePagedParagraph(string text, int paragraphIndex)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, _fontSize * 0.62),
            FontFamily = _readerFontFamily,
            FontSize = _fontSize,
            Foreground = GetBrush("PrimaryTextBrush", "#3F4B52"),
            LineHeight = _fontSize * _lineHeight,
            TextAlignment = TextAlignment.Justify,
            TextIndent = _fontSize * 2,
            KeepTogether = false,
            KeepWithNext = false
        };

        _pageParagraphIndices[paragraph] = paragraphIndex;
        var segments = new List<PageRunSegment>();
        _pageRunSegments[paragraphIndex] = segments;

        var match = GetCurrentSearchMatch();
        if (match is not null
            && match.ChapterIndex == _chapterIndex
            && match.ParagraphIndex == paragraphIndex
            && !string.IsNullOrEmpty(_activeSearchKeyword)
            && match.CharacterIndex >= 0
            && match.CharacterIndex + _activeSearchKeyword.Length <= text.Length)
        {
            AddPagedRun(paragraph, segments, text, 0, match.CharacterIndex, false);
            AddPagedRun(
                paragraph,
                segments,
                text,
                match.CharacterIndex,
                _activeSearchKeyword.Length,
                true);
            var remainingStart = match.CharacterIndex + _activeSearchKeyword.Length;
            AddPagedRun(paragraph, segments, text, remainingStart, text.Length - remainingStart, false);
        }
        else
        {
            AddPagedRun(paragraph, segments, text, 0, text.Length, false);
        }

        return paragraph;
    }

    private static void AddPagedRun(
        Paragraph paragraph,
        ICollection<PageRunSegment> segments,
        string source,
        int start,
        int length,
        bool highlighted)
    {
        if (length <= 0)
        {
            return;
        }

        var run = new Run(source.Substring(start, length));
        if (highlighted)
        {
            run.Background = GetBrush("LimeBrush", "#C4EAB5");
        }

        paragraph.Inlines.Add(run);
        segments.Add(new PageRunSegment(start, length, run));
    }

    private bool TryGetPageNumber(PageAnchor anchor, out int pageNumber)
    {
        pageNumber = 0;
        if (_pagePaginator is null || GetTextPosition(anchor) is not { } position)
        {
            return false;
        }

        try
        {
            pageNumber = Math.Max(0, _pagePaginator.GetPageNumber(position));
            _pageCount = Math.Max(_pageCount, Math.Max(_pagePaginator.PageCount, pageNumber + 1));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private TextPointer? GetTextPosition(PageAnchor anchor)
    {
        if (_pageDocument is null
            || !_pageRunSegments.TryGetValue(anchor.ParagraphIndex, out var segments))
        {
            return null;
        }

        var paragraph = _pageParagraphIndices
            .FirstOrDefault(item => item.Value == anchor.ParagraphIndex)
            .Key;
        if (paragraph is null)
        {
            return null;
        }

        var sourceLength = segments.Count == 0
            ? 0
            : segments.Max(segment => segment.StartCharacter + segment.Length);
        var characterIndex = Math.Clamp(anchor.CharacterIndex, 0, sourceLength);
        foreach (var segment in segments)
        {
            var segmentEnd = segment.StartCharacter + segment.Length;
            if (characterIndex > segmentEnd)
            {
                continue;
            }

            return segment.Run.ContentStart.GetPositionAtOffset(
                       Math.Clamp(characterIndex - segment.StartCharacter, 0, segment.Length),
                       LogicalDirection.Forward)
                   ?? segment.Run.ContentEnd;
        }

        return paragraph.ContentEnd;
    }

    private PageAnchor? CaptureCurrentPageAnchor()
    {
        if (_pagePaginator is null || _pageDocument is null || _isPagePaginationPending)
        {
            return null;
        }

        DocumentPage page;
        try
        {
            page = _pagePaginator.GetPage(_pageIndex);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        if (ReferenceEquals(page, DocumentPage.Missing)
            || _pagePaginator.GetPagePosition(page) is not TextPointer pagePosition)
        {
            return null;
        }

        var paragraph = pagePosition.Paragraph;
        if (paragraph is null || !_pageParagraphIndices.TryGetValue(paragraph, out var paragraphIndex))
        {
            paragraph = _pageParagraphIndices.Keys
                .Where(item => item.ContentEnd.CompareTo(pagePosition) >= 0)
                .OrderBy(item => _pageParagraphIndices[item])
                .FirstOrDefault();
            if (paragraph is null || !_pageParagraphIndices.TryGetValue(paragraph, out paragraphIndex))
            {
                return new PageAnchor(0, 0);
            }
        }

        var characterIndex = 0;
        if (pagePosition.CompareTo(paragraph.ContentStart) > 0
            && pagePosition.CompareTo(paragraph.ContentEnd) < 0)
        {
            characterIndex = new TextRange(paragraph.ContentStart, pagePosition).Text.Length;
        }

        return new PageAnchor(paragraphIndex, Math.Max(0, characterIndex));
    }

    private bool ShowPage(int pageNumber, int animateDirection)
    {
        if (_pagePaginator is null || _isPagePaginationPending || pageNumber < 0)
        {
            return false;
        }

        DocumentPage page;
        try
        {
            page = _pagePaginator.GetPage(pageNumber);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        if (ReferenceEquals(page, DocumentPage.Missing))
        {
            _pageCount = Math.Max(1, _pagePaginator.PageCount);
            return false;
        }

        _pageIndex = pageNumber;
        _pageCount = Math.Max(_pageCount, Math.Max(_pagePaginator.PageCount, pageNumber + 1));
        ReaderPageView.PageNumber = pageNumber;
        AnimatePageTurn(animateDirection);
        UpdateProgress();
        return true;
    }

    private void PagePaginator_PaginationProgress(object? sender, PaginationProgressEventArgs e)
    {
        if (!ReferenceEquals(sender, _pagePaginator))
        {
            return;
        }

        _pageCount = Math.Max(_pageCount, e.Start + e.Count);
        UpdateProgress();
    }

    private void PagePaginator_PaginationCompleted(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _pagePaginator) || _pagePaginator is null)
        {
            return;
        }

        var paginator = _pagePaginator;
        var anchor = _pendingRestorePageAnchor;
        var progress = _pendingPageProgress;
        _pendingRestorePageAnchor = null;
        _pendingPageProgress = null;
        _pageCount = Math.Max(1, paginator.PageCount);
        _isPagePaginationPending = false;

        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(paginator, _pagePaginator) || _readingMode != ReadingMode.Page)
            {
                return;
            }

            if (anchor is not null && TryGetPageNumber(anchor, out var targetPage))
            {
                ShowPage(targetPage, animateDirection: 0);
            }
            else if (progress is not null)
            {
                var progressPage = Math.Clamp(
                    (int)Math.Floor(progress.Value * _pageCount),
                    0,
                    _pageCount - 1);
                ShowPage(progressPage, animateDirection: 0);
            }

            UpdateProgress();
        }, DispatcherPriority.ContextIdle);
    }

    private void DetachPagePaginator()
    {
        if (_pagePaginator is not null)
        {
            _pagePaginator.PaginationProgress -= PagePaginator_PaginationProgress;
            _pagePaginator.PaginationCompleted -= PagePaginator_PaginationCompleted;
            if (_pagePaginationUserState is not null)
            {
                _pagePaginator.CancelAsync(_pagePaginationUserState);
            }
        }

        ReaderPageView.DocumentPaginator = null;
        _pagePaginator = null;
        _pageDocument = null;
        _pendingRestorePageAnchor = null;
        _pendingPageProgress = null;
        _pagePaginationUserState = null;
        _isPagePaginationPending = false;
    }

    private TextBlock CreateParagraphBlock(string paragraph, int index, int sourceStart = 0)
    {
        var block = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, _fontSize * (_readingMode == ReadingMode.Page ? 0.62 : 0.85)),
            FontFamily = _readerFontFamily,
            FontSize = _fontSize,
            Foreground = GetBrush("PrimaryTextBrush", "#3F4B52"),
            LineHeight = _fontSize * _lineHeight,
            TextAlignment = TextAlignment.Justify,
            TextWrapping = TextWrapping.Wrap,
            Tag = index
        };

        if (sourceStart == 0)
        {
            block.Inlines.Add(new Run("\u3000\u3000"));
        }

        if (GetCurrentSearchMatch() is { } match
            && match.ChapterIndex == _chapterIndex
            && match.ParagraphIndex == index
            && !string.IsNullOrEmpty(_activeSearchKeyword)
            && match.CharacterIndex >= sourceStart
            && match.CharacterIndex + _activeSearchKeyword.Length <= sourceStart + paragraph.Length)
        {
            var localMatchIndex = match.CharacterIndex - sourceStart;
            if (localMatchIndex > 0)
            {
                block.Inlines.Add(new Run(paragraph[..localMatchIndex]));
            }

            block.Inlines.Add(new Run(paragraph.Substring(localMatchIndex, _activeSearchKeyword.Length))
            {
                Background = GetBrush("LimeBrush", "#C4EAB5")
            });

            var remainingStart = localMatchIndex + _activeSearchKeyword.Length;
            if (remainingStart < paragraph.Length)
            {
                block.Inlines.Add(new Run(paragraph[remainingStart..]));
            }
        }
        else
        {
            block.Inlines.Add(new Run(paragraph));
        }

        return block;
    }

    private SearchMatch? GetCurrentSearchMatch()
    {
        return _searchIndex >= 0 && _searchIndex < _searchMatches.Count
            ? _searchMatches[_searchIndex]
            : null;
    }

    private void AddEmptyParagraph()
    {
        ArticlePanel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 18),
            Foreground = GetBrush("SecondaryTextBrush", "#81909A"),
            FontSize = _fontSize,
            FontFamily = _readerFontFamily,
            Text = "这一章暂时没有正文内容。",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void AnimatePageTurn(int direction)
    {
        if (direction == 0 || !IsLoaded)
        {
            ReaderPageView.Opacity = 1;
            ReaderPageTranslate.X = 0;
            return;
        }

        ReaderPageTranslate.X = direction > 0 ? 28 : -28;
        ReaderPageView.Opacity = 0.42;
        var duration = TimeSpan.FromMilliseconds(170);
        ReaderPageTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        ReaderPageView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void RebuildCurrentChapter(bool keepProgress)
    {
        if (keepProgress && _readingMode == ReadingMode.Page)
        {
            var anchor = CaptureCurrentPageAnchor();
            NavigateToChapter(
                _chapterIndex,
                paragraphIndex: anchor?.ParagraphIndex ?? -1,
                characterIndex: anchor?.CharacterIndex ?? -1);
            return;
        }

        var progress = keepProgress ? GetChapterProgress() : 0;
        NavigateToChapter(_chapterIndex, progress);
    }

    private void ScrollToParagraph(int paragraphIndex)
    {
        foreach (var child in ArticlePanel.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is int index && index == paragraphIndex)
            {
                child.BringIntoView();
                return;
            }
        }

        ScrollToChapterProgress(0);
    }

    private void ScrollToChapterProgress(double progress)
    {
        _suppressScrollProgress = true;
        var scrollable = GetScrollableHeight();
        ReaderScrollViewer.ScrollToVerticalOffset(Math.Clamp(progress, 0, 1) * scrollable);
        _suppressScrollProgress = false;
        UpdateProgress();
    }

    private double GetScrollableHeight()
    {
        return Math.Max(0, ReaderScrollViewer.ExtentHeight - ReaderScrollViewer.ViewportHeight);
    }

    private double GetChapterProgress()
    {
        if (_readingMode == ReadingMode.Page)
        {
            return _pageCount <= 0 ? 0 : Math.Clamp(_pageIndex / (double)_pageCount, 0, 1);
        }

        var scrollable = GetScrollableHeight();
        return scrollable <= 0 ? 0 : Math.Clamp(ReaderScrollViewer.VerticalOffset / scrollable, 0, 1);
    }

    private void UpdateProgress()
    {
        if (_document.ChapterCount == 0)
        {
            ProgressPercentText.Text = "0%";
            ProgressPositionText.Text = "0 / 0";
            TotalProgressFill.Width = 0;
            Canvas.SetLeft(TotalProgressThumb, 0);
            ChapterProgressFill.Height = 0;
            Canvas.SetTop(ChapterProgressThumb, 0);
            return;
        }

        var chapterProgress = GetChapterProgress();
        var totalProgress = (_chapterIndex + chapterProgress) / _document.ChapterCount;
        var percent = Math.Clamp(totalProgress, 0, 1);
        ProgressPercentText.Text = $"{Math.Round(percent * 100):0}%";
        ProgressPositionText.Text = _readingMode == ReadingMode.Page && _pageCount > 0
            ? $"{_chapterIndex + 1} / {_document.ChapterCount} · {_pageIndex + 1} / {_pageCount}"
            : $"{_chapterIndex + 1} / {_document.ChapterCount}";

        var trackWidth = Math.Max(0, TotalProgressTrack.ActualWidth);
        var thumbWidth = TotalProgressThumb.ActualWidth > 0 ? TotalProgressThumb.ActualWidth : 10;
        var fillWidth = trackWidth * percent;
        TotalProgressFill.Width = fillWidth;
        Canvas.SetLeft(TotalProgressThumb, Math.Max(0, Math.Min(trackWidth - thumbWidth, fillWidth - thumbWidth / 2)));

        var chapterTrackHeight = Math.Max(0, ChapterProgressTrack.ActualHeight);
        var chapterThumbHeight = ChapterProgressThumb.ActualHeight > 0 ? ChapterProgressThumb.ActualHeight : 42;
        var chapterFillHeight = chapterTrackHeight * chapterProgress;
        ChapterProgressFill.Height = chapterFillHeight;
        Canvas.SetTop(ChapterProgressThumb, Math.Max(0, Math.Min(chapterTrackHeight - chapterThumbHeight, chapterFillHeight - chapterThumbHeight / 2)));
        ScheduleReadingProgressSave();
    }

    private void ScheduleReadingProgressSave()
    {
        if (!IsLoaded || _document.ChapterCount == 0)
        {
            return;
        }

        _progressSaveTimer.Stop();
        _progressSaveTimer.Start();
    }

    private void SaveCurrentReadingProgress()
    {
        if (_document.ChapterCount == 0 || (_readingMode == ReadingMode.Page && _isPagePaginationPending))
        {
            return;
        }

        var chapterIndex = Math.Clamp(_chapterIndex, 0, _document.ChapterCount - 1);
        var chapterProgress = Math.Round(Math.Clamp(GetChapterProgress(), 0, 1), 6);
        if (_book.LastReadChapterIndex == chapterIndex
            && Math.Abs(_book.LastReadChapterProgress - chapterProgress) < 0.000001)
        {
            return;
        }

        _book.LastReadChapterIndex = chapterIndex;
        _book.LastReadChapterProgress = chapterProgress;
        _saveState();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_readingMode == ReadingMode.Page)
        {
            PreviousPage();
            return;
        }

        PreviousChapter();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_readingMode == ReadingMode.Page)
        {
            NextPage();
            return;
        }

        NextChapter();
    }

    private void PreviousChapter()
    {
        if (_chapterIndex > 0)
        {
            NavigateToChapter(_chapterIndex - 1, 0.98);
        }
    }

    private void NextChapter()
    {
        if (_chapterIndex < _document.ChapterCount - 1)
        {
            NavigateToChapter(_chapterIndex + 1);
        }
    }

    private void PreviousPage()
    {
        if (_readingMode == ReadingMode.Page)
        {
            if (_isPagePaginationPending)
            {
                return;
            }

            _pendingRestorePageAnchor = null;
            _pendingPageProgress = null;
            if (_pageIndex > 0)
            {
                ShowPage(_pageIndex - 1, animateDirection: -1);
                return;
            }

            PreviousChapter();
            return;
        }

        var target = ReaderScrollViewer.VerticalOffset - GetPageStep();
        if (target > 0)
        {
            ReaderScrollViewer.ScrollToVerticalOffset(target);
        }
        else
        {
            PreviousChapter();
        }
    }

    private void NextPage()
    {
        if (_readingMode == ReadingMode.Page)
        {
            if (_isPagePaginationPending)
            {
                return;
            }

            _pendingRestorePageAnchor = null;
            _pendingPageProgress = null;
            if (ShowPage(_pageIndex + 1, animateDirection: 1))
            {
                return;
            }

            NextChapter();
            return;
        }

        var target = ReaderScrollViewer.VerticalOffset + GetPageStep();
        if (target < GetScrollableHeight())
        {
            ReaderScrollViewer.ScrollToVerticalOffset(target);
        }
        else
        {
            NextChapter();
        }
    }

    private double GetPageStep()
    {
        return Math.Max(260, ReaderScrollViewer.ViewportHeight * 0.88);
    }

    private void ApplyReadingMode(ReadingMode mode, bool save = true)
    {
        var progress = _document.ChapterCount > 0 ? GetChapterProgress() : 0;
        _readingMode = mode;
        ReaderScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        PreviousButtonText.Text = mode == ReadingMode.Page ? "上一页" : "上一章";
        NextButtonText.Text = mode == ReadingMode.Page ? "下一页" : "下一章";
        ScrollModeButton.CommandParameter = mode == ReadingMode.Scroll ? "Active" : null;
        PageModeButton.CommandParameter = mode == ReadingMode.Page ? "Active" : null;

        if (IsLoaded || ArticlePanel.Children.Count > 0 || _pagePaginator is not null)
        {
            NavigateToChapter(_chapterIndex, progress);
        }

        if (save)
        {
            SaveReaderPreferences();
        }
    }

    private void SetScrollMode_Click(object sender, RoutedEventArgs e) => ApplyReadingMode(ReadingMode.Scroll);

    private void SetPageMode_Click(object sender, RoutedEventArgs e) => ApplyReadingMode(ReadingMode.Page);

    private void ToggleToc_Click(object sender, RoutedEventArgs e)
    {
        TogglePanel(TocPanel, TocButton);
    }

    private void ToggleBookmarks_Click(object sender, RoutedEventArgs e)
    {
        RefreshBookmarks();
        TogglePanel(BookmarkPanel, BookmarkButton);
    }

    private void ToggleSettings_Click(object sender, RoutedEventArgs e)
    {
        TogglePanel(SettingsPanel, SettingsPanelButton);
    }

    private void ToggleSearch_Click(object sender, RoutedEventArgs e)
    {
        ClosePanels();
        var shouldOpen = SearchPanel.Visibility != Visibility.Visible;
        SearchPanel.Visibility = shouldOpen ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.CommandParameter = shouldOpen ? "Active" : null;
        if (shouldOpen)
        {
            RefreshSearchResultsFromInput();
            ReaderSearchBox.Focus();
            ReaderSearchBox.SelectAll();
        }
    }

    private void TogglePanel(UIElement panel, Button button)
    {
        var shouldOpen = panel.Visibility != Visibility.Visible;
        ClosePanels();
        CloseSearch();

        if (shouldOpen)
        {
            Overlay.Visibility = Visibility.Visible;
            panel.Visibility = Visibility.Visible;
            button.CommandParameter = "Active";
        }
    }

    private void ClosePanels()
    {
        Overlay.Visibility = Visibility.Collapsed;
        TocPanel.Visibility = Visibility.Collapsed;
        BookmarkPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        TocButton.CommandParameter = null;
        BookmarkButton.CommandParameter = null;
        SettingsPanelButton.CommandParameter = null;
    }

    private void CloseSearch()
    {
        _searchDebounceTimer.Stop();
        SearchPanel.Visibility = Visibility.Collapsed;
        SearchButton.CommandParameter = null;
    }

    private void ClosePanels_Click(object sender, RoutedEventArgs e)
    {
        ClosePanels();
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ClosePanels();
    }

    private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChapterList.SelectedIndex < 0)
        {
            return;
        }

        if (ChapterList.SelectedIndex != _chapterIndex)
        {
            NavigateToChapter(ChapterList.SelectedIndex);
        }

        ClosePanels();
    }

    private void RefreshBookmarks()
    {
        _currentBookmarks.Clear();
        _currentBookmarks.AddRange(_state.Bookmarks
            .Where(bookmark => string.Equals(bookmark.FilePath, _book.FilePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(bookmark => bookmark.ChapterIndex)
            .ThenBy(bookmark => bookmark.ChapterProgress));

        BookmarkList.ItemsSource = null;
        BookmarkList.ItemsSource = _currentBookmarks;
        BookmarkList.Visibility = _currentBookmarks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BookmarkEmptyState.Visibility = _currentBookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteBookmarkButton.IsEnabled = false;
        RenameBookmarkButton.IsEnabled = false;
    }

    private void BookmarkList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = BookmarkList.SelectedItem as ReaderBookmark;
        DeleteBookmarkButton.IsEnabled = selected is not null;
        RenameBookmarkButton.IsEnabled = selected is not null;
        if (selected is not null)
        {
            BookmarkNameBox.Text = selected.Title;
        }
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_document.ChapterCount == 0)
        {
            return;
        }

        var chapter = _document.Chapters[_chapterIndex];
        var progress = GetChapterProgress();
        var percent = Math.Round(progress * 100);
        var customTitle = BookmarkNameBox.Text.Trim();
        _state.Bookmarks.Add(new ReaderBookmark
        {
            FilePath = _book.FilePath,
            ChapterIndex = _chapterIndex,
            ChapterProgress = progress,
            Title = string.IsNullOrWhiteSpace(customTitle)
                ? $"第 {chapter.Number:00} 章 · {percent:0}% · {chapter.Title}"
                : customTitle,
            CreatedAt = DateTime.Now
        });

        RefreshBookmarks();
        BookmarkNameBox.Clear();
        _saveState();
    }

    private void RenameBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkList.SelectedItem is not ReaderBookmark bookmark)
        {
            return;
        }

        var title = BookmarkNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            BookmarkNameBox.Focus();
            return;
        }

        bookmark.Title = title;
        RefreshBookmarks();
        BookmarkNameBox.Clear();
        _saveState();
    }

    private void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkList.SelectedIndex < 0 || BookmarkList.SelectedIndex >= _currentBookmarks.Count)
        {
            return;
        }

        var bookmark = _currentBookmarks[BookmarkList.SelectedIndex];
        _state.Bookmarks.Remove(bookmark);
        RefreshBookmarks();
        BookmarkNameBox.Clear();
        _saveState();
    }

    private void BookmarkList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BookmarkList.SelectedIndex < 0 || BookmarkList.SelectedIndex >= _currentBookmarks.Count)
        {
            return;
        }

        var bookmark = _currentBookmarks[BookmarkList.SelectedIndex];
        NavigateToChapter(bookmark.ChapterIndex, bookmark.ChapterProgress);
        ClosePanels();
    }

    private void ReaderSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void ReaderSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Search(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
    }

    private void SearchPrevious_Click(object sender, RoutedEventArgs e) => Search(-1);

    private void SearchNext_Click(object sender, RoutedEventArgs e) => Search(1);

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void Search(int direction)
    {
        var keyword = ReaderSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            SearchStatusText.Text = "请输入";
            return;
        }

        EnsureSearchMatches(keyword);
        if (_searchMatches.Count == 0)
        {
            SearchStatusText.Text = "0 / 0";
            return;
        }

        if (_searchIndex < 0)
        {
            _searchIndex = direction > 0 ? 0 : _searchMatches.Count - 1;
        }
        else
        {
            _searchIndex = (_searchIndex + direction + _searchMatches.Count) % _searchMatches.Count;
        }

        var match = _searchMatches[_searchIndex];
        ActivateSearchMatch(match, updateSelection: true);
    }

    private void RefreshSearchResultsFromInput()
    {
        var keyword = ReaderSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            _activeSearchKeyword = string.Empty;
            _searchIndex = -1;
            _searchMatches.Clear();
            BindSearchResults();
            return;
        }

        EnsureSearchMatches(keyword);
    }

    private void EnsureSearchMatches(string keyword)
    {
        if (string.Equals(_activeSearchKeyword, keyword, StringComparison.CurrentCultureIgnoreCase))
        {
            return;
        }

        _activeSearchKeyword = keyword;
        _searchIndex = -1;
        _searchMatches.Clear();

        for (var chapterIndex = 0; chapterIndex < _document.ChapterCount; chapterIndex++)
        {
            var chapter = _document.Chapters[chapterIndex];
            AddSearchMatches(chapter.Title, keyword, chapterIndex, -1, chapter.Number, chapter.Title);

            for (var paragraphIndex = 0; paragraphIndex < chapter.Paragraphs.Count; paragraphIndex++)
            {
                AddSearchMatches(
                    chapter.Paragraphs[paragraphIndex],
                    keyword,
                    chapterIndex,
                    paragraphIndex,
                    chapter.Number,
                    chapter.Title);
                if (_searchMatches.Count >= MaxSearchMatches)
                {
                    break;
                }
            }

            if (_searchMatches.Count >= MaxSearchMatches)
            {
                break;
            }
        }

        BindSearchResults();
    }

    private void AddSearchMatches(
        string text,
        string keyword,
        int chapterIndex,
        int paragraphIndex,
        int chapterNumber,
        string chapterTitle)
    {
        var searchStart = 0;
        while (searchStart <= text.Length - keyword.Length && _searchMatches.Count < MaxSearchMatches)
        {
            var characterIndex = text.IndexOf(
                keyword,
                searchStart,
                StringComparison.CurrentCultureIgnoreCase);
            if (characterIndex < 0)
            {
                break;
            }

            _searchMatches.Add(new SearchMatch(
                chapterIndex,
                paragraphIndex,
                characterIndex,
                $"第 {chapterNumber:00} 章 · {chapterTitle}",
                BuildSearchPreview(text, characterIndex, keyword.Length)));
            searchStart = characterIndex + keyword.Length;
        }
    }

    private void BindSearchResults()
    {
        _suppressSearchSelection = true;
        SearchResultsList.ItemsSource = null;
        SearchResultsList.ItemsSource = _searchMatches;
        SearchResultsList.SelectedIndex = _searchIndex;
        _suppressSearchSelection = false;

        SearchResultsList.Visibility = _searchMatches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchEmptyState.Visibility = _searchMatches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchEmptyHint.Text = string.IsNullOrWhiteSpace(_activeSearchKeyword)
            ? "输入关键词开始搜索"
            : "没有找到匹配内容";
        SearchStatusText.Text = _searchMatches.Count >= MaxSearchMatches
            ? $"{MaxSearchMatches}+ 个结果"
            : $"{_searchMatches.Count} 个结果";
    }

    private void SearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSearchSelection || SearchResultsList.SelectedItem is not SearchMatch match)
        {
            return;
        }

        _searchIndex = SearchResultsList.SelectedIndex;
        ActivateSearchMatch(match, updateSelection: false);
    }

    private void ActivateSearchMatch(SearchMatch match, bool updateSelection)
    {
        NavigateToChapter(match.ChapterIndex, 0, match.ParagraphIndex, match.CharacterIndex);
        if (updateSelection)
        {
            _suppressSearchSelection = true;
            SearchResultsList.SelectedIndex = _searchIndex;
            SearchResultsList.ScrollIntoView(match);
            _suppressSearchSelection = false;
        }

        SearchStatusText.Text = $"{_searchIndex + 1} / {_searchMatches.Count}";
    }

    private static string BuildSearchPreview(string text, int characterIndex, int keywordLength)
    {
        var compact = string.Join(
            ' ',
            text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length <= 72)
        {
            return compact;
        }

        var approximateIndex = Math.Min(characterIndex, compact.Length - 1);
        var start = Math.Max(0, approximateIndex - 28);
        var length = Math.Min(compact.Length - start, keywordLength + 56);
        return $"{(start > 0 ? "…" : string.Empty)}{compact.Substring(start, length)}{(start + length < compact.Length ? "…" : string.Empty)}";
    }

    private void DecreaseFont_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = Math.Max(12, _fontSize - 1);
        UpdateSettingText();
        RebuildCurrentChapter(keepProgress: true);
        SaveReaderPreferences();
    }

    private void IncreaseFont_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = Math.Min(32, _fontSize + 1);
        UpdateSettingText();
        RebuildCurrentChapter(keepProgress: true);
        SaveReaderPreferences();
    }

    private void DecreaseLineHeight_Click(object sender, RoutedEventArgs e)
    {
        _lineHeight = Math.Max(1.2, _lineHeight - 0.05);
        UpdateSettingText();
        RebuildCurrentChapter(keepProgress: true);
        SaveReaderPreferences();
    }

    private void IncreaseLineHeight_Click(object sender, RoutedEventArgs e)
    {
        _lineHeight = Math.Min(2.8, _lineHeight + 0.05);
        UpdateSettingText();
        RebuildCurrentChapter(keepProgress: true);
        SaveReaderPreferences();
    }

    private void DecreaseContentWidth_Click(object sender, RoutedEventArgs e)
    {
        _contentWidthPercent = Math.Max(
            MinContentWidthPercent,
            _contentWidthPercent - ContentWidthPercentStep);
        UpdateSettingText();
        RebuildCurrentChapter(keepProgress: true);
        SaveReaderPreferences();
    }

    private void IncreaseContentWidth_Click(object sender, RoutedEventArgs e)
    {
        _contentWidthPercent = Math.Min(
            MaxContentWidthPercent,
            _contentWidthPercent + ContentWidthPercentStep);
        UpdateSettingText();
        RebuildCurrentChapter(keepProgress: true);
        SaveReaderPreferences();
    }

    private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontFamilyCombo.SelectedItem is not FontFamily fontFamily)
        {
            return;
        }

        _readerFontFamily = fontFamily;
        if (IsLoaded)
        {
            RebuildCurrentChapter(keepProgress: true);
        }

        SaveReaderPreferences();
    }

    private void UpdateSettingText()
    {
        FontSizeText.Text = $"{_fontSize:0} px";
        LineHeightText.Text = $"{_lineHeight:0.00}";
        ContentWidthText.Text = $"{_contentWidthPercent:0}%";
        AutoPageIntervalText.Text = $"{_autoPageIntervalSeconds:0.#} 秒";
    }

    private void ToggleAutoPage_Click(object sender, RoutedEventArgs e)
    {
        SetAutoPageEnabled(!_autoPageEnabled);
    }

    private void SetAutoPageEnabled(bool enabled, bool save = true)
    {
        _autoPageEnabled = enabled;
        ConfigureAutoPageTimer();
        UpdateAutoPageControls();
        if (save)
        {
            SaveReaderPreferences();
        }
    }

    private void ConfigureAutoPageTimer()
    {
        _autoPageTimer.Stop();
        _autoPageTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_autoPageIntervalSeconds, 1, 3600));
        if (_autoPageEnabled && IsLoaded)
        {
            _autoPageTimer.Start();
        }
    }

    private void UpdateAutoPageControls()
    {
        AutoPageButton.CommandParameter = _autoPageEnabled ? "Active" : null;
        AutoPageButton.Tag = _autoPageEnabled ? "暂停翻页" : "自动翻页";
        AutoPageButton.ToolTip = _autoPageEnabled ? "暂停自动翻页" : "开启自动翻页";
        AutoPageIcon.Text = _autoPageEnabled ? "Ⅱ" : "▶";
        AutoPageIntervalText.Text = $"{_autoPageIntervalSeconds:0.#} 秒";
    }

    private void ToggleImmersiveMode_Click(object sender, RoutedEventArgs e)
    {
        SetImmersiveMode(!_immersiveMode);
    }

    private void SetImmersiveMode(bool enabled, bool save = true)
    {
        _immersiveMode = enabled;
        _immersiveHideTimer.Stop();
        SetChromeVisible(!enabled);
        UpdateImmersiveModeControls();
        if (save)
        {
            SaveReaderPreferences();
        }
    }

    private void UpdateImmersiveModeControls()
    {
        ImmersiveModeButton.CommandParameter = _immersiveMode ? "Active" : null;
        ImmersiveModeButton.Tag = _immersiveMode ? "退出专注" : "专注模式";
        ImmersiveModeButton.ToolTip = _immersiveMode ? "退出专注模式" : "开启专注模式";
        ImmersiveModeIcon.Text = _immersiveMode ? "◱" : "⛶";
        ImmersiveCloseButton.Visibility = _immersiveMode
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DecreaseAutoPageInterval_Click(object sender, RoutedEventArgs e)
    {
        _autoPageIntervalSeconds = Math.Max(1, _autoPageIntervalSeconds - 1);
        ConfigureAutoPageTimer();
        UpdateAutoPageControls();
        SaveReaderPreferences();
    }

    private void IncreaseAutoPageInterval_Click(object sender, RoutedEventArgs e)
    {
        _autoPageIntervalSeconds = Math.Min(3600, _autoPageIntervalSeconds + 1);
        ConfigureAutoPageTimer();
        UpdateAutoPageControls();
        SaveReaderPreferences();
    }

    private void AutoPageTimer_Tick(object? sender, EventArgs e)
    {
        if (!_autoPageEnabled || !IsVisible || AnyPanelOpen() || _isPagePaginationPending)
        {
            return;
        }

        var previousChapter = _chapterIndex;
        var previousPage = _pageIndex;
        var previousOffset = ReaderScrollViewer.VerticalOffset;
        NextPage();
        var advanced = previousChapter != _chapterIndex
                       || (_readingMode == ReadingMode.Page && previousPage != _pageIndex)
                       || (_readingMode == ReadingMode.Scroll
                           && Math.Abs(previousOffset - ReaderScrollViewer.VerticalOffset) > 0.5);
        if (!advanced)
        {
            SetAutoPageEnabled(false);
        }
    }

    private void ApplyContentWidth()
    {
        ArticlePanel.Width = GetResponsiveContentWidth();
    }

    private double GetResponsiveContentWidth()
    {
        if (ReaderBody.ActualWidth <= 0)
        {
            return FallbackContentWidth;
        }

        var horizontalInset = _isChromeVisible ? ReaderHorizontalInset : ImmersiveHorizontalInset;
        var availableWidth = Math.Max(320, ReaderBody.ActualWidth - horizontalInset * 2);
        return availableWidth * (_contentWidthPercent / 100);
    }

    private void SetWhiteTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme("white");

    private void SetEyeTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme("eye");

    private void SetNightTheme_Click(object sender, RoutedEventArgs e) => ApplyTheme("night");

    private void ApplyTheme(string theme, bool save = true)
    {
        _theme = theme;
        WhiteThemeButton.CommandParameter = theme == "white" ? "Active" : null;
        EyeThemeButton.CommandParameter = theme == "eye" ? "Active" : null;
        NightThemeButton.CommandParameter = theme == "night" ? "Active" : null;

        switch (theme)
        {
            case "eye":
                SetBrush("MainBackgroundBrush", "#FBFCF5");
                SetBrush("ReaderBackgroundBrush", "#F5F6ED");
                SetBrush("PrimaryTextBrush", "#4D5148");
                SetBrush("SecondaryTextBrush", "#858A7D");
                SetBrush("FaintTextBrush", "#AAB0A2");
                SetBrush("LineBrush", "#E3E7D8");
                SetBrush("AccentBrush", "#C8DDC0");
                SetBrush("AccentDarkBrush", "#66755F");
                SetBrush("AccentSoftBrush", "#F2F5E9");
                SetBrush("ControlSurfaceBrush", "#F0F2E8");
                SetBrush("ControlHoverBrush", "#E8ECE0");
                SetBrush("LimeBrush", "#CFEBC3");
                SetBrush("LimeDarkBrush", "#5C7655");
                SetBrush("SidebarTextBrush", "#647A62");
                SetBrush("SidebarActiveTextBrush", "#405D43");
                SetBrush("SidebarHoverBrush", "#4DFFFFFF");
                SetBrush("SidebarSelectedBrush", "#E8FFFFFF");
                SetBrush("SidebarEdgeBrush", "#66FFFFFF");
                SetSidebarBrush("#CADDB8", "#D5E6C9", "#E2EED8");
                ReaderRoot.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F6ED"));
                ReaderTopBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBFCF5"));
                ReaderBottomBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBFCF5"));
                Overlay.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3DE7ECDD"));
                break;
            case "night":
                SetBrush("MainBackgroundBrush", "#202526");
                SetBrush("ReaderBackgroundBrush", "#252A2B");
                SetBrush("PrimaryTextBrush", "#DDE5E3");
                SetBrush("SecondaryTextBrush", "#9EAAA7");
                SetBrush("FaintTextBrush", "#6F7B78");
                SetBrush("LineBrush", "#343D3E");
                SetBrush("AccentBrush", "#58787A");
                SetBrush("AccentDarkBrush", "#A8CFD0");
                SetBrush("AccentSoftBrush", "#2B3536");
                SetBrush("ControlSurfaceBrush", "#292F30");
                SetBrush("ControlHoverBrush", "#333B3C");
                SetBrush("LimeBrush", "#416B57");
                SetBrush("LimeDarkBrush", "#D5EBDD");
                SetBrush("SidebarTextBrush", "#AAC4C6");
                SetBrush("SidebarActiveTextBrush", "#E3F0EF");
                SetBrush("SidebarHoverBrush", "#22FFFFFF");
                SetBrush("SidebarSelectedBrush", "#385258");
                SetBrush("SidebarEdgeBrush", "#3D5960");
                SetSidebarBrush("#24383C", "#2D4448", "#385257");
                ReaderRoot.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252A2B"));
                ReaderTopBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202526"));
                ReaderBottomBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202526"));
                Overlay.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#700D1112"));
                break;
            default:
                SetBrush("MainBackgroundBrush", "#FFFFFF");
                SetBrush("ReaderBackgroundBrush", "#F7FAFC");
                SetBrush("PrimaryTextBrush", "#3F4B52");
                SetBrush("SecondaryTextBrush", "#81909A");
                SetBrush("FaintTextBrush", "#AAB6BD");
                SetBrush("LineBrush", "#E5EEF3");
                SetBrush("AccentBrush", "#B9DDF0");
                SetBrush("AccentDarkBrush", "#4E7185");
                SetBrush("AccentSoftBrush", "#F2F8FC");
                SetBrush("ControlSurfaceBrush", "#F2F7FA");
                SetBrush("ControlHoverBrush", "#E5F1F7");
                SetBrush("LimeBrush", "#C4EAB5");
                SetBrush("LimeDarkBrush", "#64805E");
                SetBrush("SidebarTextBrush", "#55798E");
                SetBrush("SidebarActiveTextBrush", "#356A88");
                SetBrush("SidebarHoverBrush", "#48FFFFFF");
                SetBrush("SidebarSelectedBrush", "#FFFFFF");
                SetBrush("SidebarEdgeBrush", "#48FFFFFF");
                SetSidebarBrush("#AFD0EC", "#B8D6EE", "#C3DCF1");
                ReaderRoot.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F7FAFC"));
                ReaderTopBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FAFFFFFF"));
                ReaderBottomBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FAFFFFFF"));
                Overlay.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#44EBF2F6"));
                break;
        }

        if (IsLoaded)
        {
            RebuildCurrentChapter(keepProgress: true);
        }

        if (save)
        {
            SaveReaderPreferences();
        }
    }

    private static void SetBrush(string resourceKey, string color)
    {
        Application.Current.Resources[resourceKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static void SetSidebarBrush(string left, string middle, string right)
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(left), 0));
        gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(middle), 0.55));
        gradient.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(right), 1));
        Application.Current.Resources["SidebarBrush"] = gradient;
    }

    private void ReaderScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TocPanel.IsMouseOver
            || BookmarkPanel.IsMouseOver
            || SettingsPanel.IsMouseOver
            || SearchPanel.IsMouseOver)
        {
            return;
        }

        if (_readingMode == ReadingMode.Page)
        {
            if (e.Delta < 0)
            {
                NextPage();
            }
            else
            {
                PreviousPage();
            }

            e.Handled = true;
            return;
        }

        ReaderScrollViewer.ScrollToVerticalOffset(ReaderScrollViewer.VerticalOffset - e.Delta * 0.42);
        e.Handled = true;
    }

    private void ReaderSettingsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ReaderSettingsScrollViewer.ScrollToVerticalOffset(
            ReaderSettingsScrollViewer.VerticalOffset - e.Delta * 0.55);
        e.Handled = true;
    }

    private void ReaderScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_suppressScrollProgress)
        {
            UpdateProgress();
        }
    }

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateProgress();
    }

    private void TotalProgressTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_document.ChapterCount == 0 || TotalProgressTrack.ActualWidth <= 0)
        {
            return;
        }

        _isDraggingTotalProgress = true;
        TotalProgressTrack.CaptureMouse();
        SeekToTotalProgress(e.GetPosition(TotalProgressTrack).X / TotalProgressTrack.ActualWidth);
        e.Handled = true;
    }

    private void TotalProgressTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingTotalProgress || e.LeftButton != MouseButtonState.Pressed || TotalProgressTrack.ActualWidth <= 0)
        {
            return;
        }

        SeekToTotalProgress(e.GetPosition(TotalProgressTrack).X / TotalProgressTrack.ActualWidth);
        e.Handled = true;
    }

    private void TotalProgressTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingTotalProgress)
        {
            return;
        }

        _isDraggingTotalProgress = false;
        TotalProgressTrack.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ChapterProgressTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_document.ChapterCount == 0 || ChapterProgressTrack.ActualHeight <= 0)
        {
            return;
        }

        _isDraggingChapterProgress = true;
        ChapterProgressTrack.CaptureMouse();
        SeekToChapterProgress(e.GetPosition(ChapterProgressTrack).Y / ChapterProgressTrack.ActualHeight);
        e.Handled = true;
    }

    private void ChapterProgressTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingChapterProgress || e.LeftButton != MouseButtonState.Pressed || ChapterProgressTrack.ActualHeight <= 0)
        {
            return;
        }

        SeekToChapterProgress(e.GetPosition(ChapterProgressTrack).Y / ChapterProgressTrack.ActualHeight);
        e.Handled = true;
    }

    private void ChapterProgressTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingChapterProgress)
        {
            return;
        }

        _isDraggingChapterProgress = false;
        ChapterProgressTrack.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SeekToTotalProgress(double value)
    {
        var percent = Math.Clamp(value, 0, 1);
        var rawChapter = percent * _document.ChapterCount;
        var chapter = Math.Clamp((int)Math.Floor(rawChapter), 0, _document.ChapterCount - 1);
        var chapterProgress = rawChapter - chapter;
        NavigateToChapter(chapter, chapterProgress);
    }

    private void SeekToChapterProgress(double value)
    {
        var progress = Math.Clamp(value, 0, 1);
        if (_readingMode == ReadingMode.Page)
        {
            if (_pagePaginator is null)
            {
                return;
            }

            if (_pagePaginator.IsPageCountValid)
            {
                _pendingRestorePageAnchor = null;
                _pendingPageProgress = null;
                _pageCount = Math.Max(1, _pagePaginator.PageCount);
                var targetPage = Math.Clamp((int)Math.Floor(progress * _pageCount), 0, _pageCount - 1);
                ShowPage(targetPage, animateDirection: 0);
            }
            else
            {
                _pendingPageProgress = progress;
            }

            return;
        }

        ScrollToChapterProgress(progress);
    }

    private void ReaderBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyContentWidth();
        if (_readingMode == ReadingMode.Page && IsLoaded)
        {
            _pendingPageAnchor = CaptureCurrentPageAnchor();
            _pageReflowTimer.Stop();
            _pageReflowTimer.Start();
            return;
        }

        UpdateProgress();
    }

    private void PageReflowTimer_Tick(object? sender, EventArgs e)
    {
        _pageReflowTimer.Stop();
        if (_readingMode != ReadingMode.Page || _document.ChapterCount == 0)
        {
            _pendingPageAnchor = null;
            return;
        }

        var anchor = _pendingPageAnchor;
        var progress = anchor is null ? GetChapterProgress() : 0;
        _pendingPageAnchor = null;
        RenderChapter(
            _document.Chapters[_chapterIndex],
            progress,
            anchor?.ParagraphIndex ?? -1,
            anchor?.CharacterIndex ?? -1);
        UpdateProgress();
    }

    private void ReaderBody_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource as DependencyObject) || AnyPanelOpen())
        {
            return;
        }

        var x = e.GetPosition(ReaderBody).X;
        var width = Math.Max(1, ReaderBody.ActualWidth);
        if (x < width * 0.28)
        {
            if (_readingMode == ReadingMode.Page)
            {
                PreviousPage();
            }
            else
            {
                PreviousChapter();
            }
            return;
        }

        if (x > width * 0.72)
        {
            if (_readingMode == ReadingMode.Page)
            {
                NextPage();
            }
            else
            {
                NextChapter();
            }
            return;
        }
    }

    private bool AnyPanelOpen()
    {
        return TocPanel.Visibility == Visibility.Visible
               || BookmarkPanel.Visibility == Visibility.Visible
               || SettingsPanel.Visibility == Visibility.Visible
               || SearchPanel.Visibility == Visibility.Visible;
    }

    private void SetChromeVisible(bool visible)
    {
        if (!visible)
        {
            _immersiveHideTimer.Stop();
            CollapseReaderRail();
            _isImmersiveRailVisible = false;
        }

        _isChromeVisible = visible;
        ClosePanels();
        CloseSearch();

        ReaderRailColumn.Width = new GridLength(visible ? 68 : 0);
        ReaderTopRow.Height = new GridLength(visible ? 58 : 0);
        ReaderBottomRow.Height = new GridLength(visible ? 56 : 0);

        var chromeVisibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ReaderRail.Visibility = chromeVisibility;
        ReaderTopBar.Visibility = chromeVisibility;
        ReaderBottomBar.Visibility = chromeVisibility;
        ChapterProgressRail.Visibility = chromeVisibility;
        TotalProgressRail.Visibility = chromeVisibility;
        FloatingWindowActions.Visibility = Visibility.Collapsed;
        ImmersiveHotZone.Visibility = _immersiveMode && !_isImmersiveRailVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyContentWidth();
    }

    private void ReaderRoot_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(ReaderRoot);
        if (_immersiveMode)
        {
            if (!_isImmersiveRailVisible)
            {
                if (position.X <= ImmersiveHotZoneWidth)
                {
                    RevealImmersiveRail();
                }
                return;
            }

            var railLimit = _readerRailExpanded ? 176 : 68;
            var overRail = position.X >= 0 && position.X <= railLimit;
            if (overRail || AnyPanelOpen())
            {
                _immersiveHideTimer.Stop();
            }
            else
            {
                ScheduleImmersiveHide();
            }

            if (!_readerRailExpanded && position.X >= 0 && position.X <= 68)
            {
                ExpandReaderRail();
            }
            else if (_readerRailExpanded && position.X > 176 && !AnyPanelOpen())
            {
                CollapseReaderRail();
            }
            return;
        }

        if (!_readerRailExpanded && position.X >= 0 && position.X <= 68)
        {
            ExpandReaderRail();
        }
        else if (_readerRailExpanded && position.X > 176)
        {
            CollapseReaderRail();
        }
    }

    private void ReaderRoot_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_immersiveMode)
        {
            HideImmersiveRail();
        }
        else
        {
            CollapseReaderRail();
        }
    }

    private void ImmersiveHotZone_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_immersiveMode)
        {
            RevealImmersiveRail();
        }
    }

    private void ReaderRail_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_immersiveMode)
        {
            _immersiveHideTimer.Stop();
        }
    }

    private void ReaderRail_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_immersiveMode && !AnyPanelOpen())
        {
            ScheduleImmersiveHide();
        }
    }

    private void RevealImmersiveRail()
    {
        if (!_immersiveMode || _isImmersiveRailVisible)
        {
            return;
        }

        _immersiveHideTimer.Stop();
        _isImmersiveRailVisible = true;
        ReaderRail.Visibility = Visibility.Visible;
        ImmersiveHotZone.Visibility = Visibility.Collapsed;
        ExpandReaderRail();
    }

    private void HideImmersiveRail()
    {
        if (!_immersiveMode)
        {
            return;
        }

        _immersiveHideTimer.Stop();
        CollapseReaderRail();
        _isImmersiveRailVisible = false;
        ReaderRail.Visibility = Visibility.Collapsed;
        ImmersiveHotZone.Visibility = Visibility.Visible;
    }

    private void ScheduleImmersiveHide()
    {
        if (!_immersiveMode || !_isImmersiveRailVisible)
        {
            return;
        }

        _immersiveHideTimer.Stop();
        _immersiveHideTimer.Start();
    }

    private void ExpandReaderRail()
    {
        if (_readerRailExpanded)
        {
            return;
        }

        _readerRailExpanded = true;
        AnimateReaderRail(176, 150, 190, EasingMode.EaseOut);
    }

    private void CollapseReaderRail()
    {
        if (!_readerRailExpanded)
        {
            return;
        }

        _readerRailExpanded = false;
        AnimateReaderRail(68, 38, 160, EasingMode.EaseInOut);
    }

    private void AnimateReaderRail(double railWidth, double buttonWidth, int durationMs, EasingMode easingMode)
    {
        ReaderRail.BeginAnimation(
            WidthProperty,
            new DoubleAnimation(ReaderRail.ActualWidth, railWidth, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = easingMode }
            },
            HandoffBehavior.SnapshotAndReplace);

        foreach (var item in ReaderNavigation.Children
                     .OfType<FrameworkElement>()
                     .Append(ImmersiveCloseButton))
        {
            item.BeginAnimation(
                WidthProperty,
                new DoubleAnimation(item.ActualWidth, buttonWidth, TimeSpan.FromMilliseconds(durationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = easingMode }
                },
                HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void Reader_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (SettingsPanel.Visibility == Visibility.Visible && IsAltKey(e))
        {
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            ToggleSearch_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_immersiveMode && _isImmersiveRailVisible && !AnyPanelOpen())
            {
                HideImmersiveRail();
            }
            else
            {
                ClosePanels();
                CloseSearch();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            Previous_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            Next_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.PageUp)
        {
            PreviousPage();
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown)
        {
            NextPage();
            e.Handled = true;
        }
    }

    private void Reader_PreviewKeyUp(object sender, WpfKeyEventArgs e)
    {
        if (SettingsPanel.Visibility == Visibility.Visible && IsAltKey(e))
        {
            e.Handled = true;
        }
    }

    private static bool IsAltKey(WpfKeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key is Key.LeftAlt or Key.RightAlt;
    }

    private void BackToShelf_Click(object sender, RoutedEventArgs e)
    {
        _progressSaveTimer.Stop();
        SaveCurrentReadingProgress();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        MinimizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseApp_Click(object sender, RoutedEventArgs e)
    {
        if (_immersiveMode)
        {
            HideImmersiveRail();
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ReaderTopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var window = Window.GetWindow(this);
        if (e.ClickCount == 2 && window is not null)
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            TryDragWindow(window);
        }
    }

    private static void TryDragWindow(Window? window)
    {
        if (window is null)
        {
            return;
        }

        if (window.WindowState == WindowState.Maximized)
        {
            window.WindowState = WindowState.Normal;
        }

        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBox or ComboBox or ListBox or ScrollBar)
            {
                return true;
            }

            source = source is FrameworkContentElement contentElement
                ? contentElement.Parent
                : VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static Brush GetBrush(string resourceKey, string fallbackColor)
    {
        return Application.Current.Resources[resourceKey] as Brush
               ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackColor));
    }
}
