using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SeaS.App;

public sealed class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText),
        typeof(string),
        typeof(HighlightTextBlock),
        new PropertyMetadata(string.Empty, OnTextPartChanged));

    public static readonly DependencyProperty HighlightTextProperty = DependencyProperty.Register(
        nameof(HighlightText),
        typeof(string),
        typeof(HighlightTextBlock),
        new PropertyMetadata(string.Empty, OnTextPartChanged));

    public static readonly DependencyProperty HighlightBackgroundProperty = DependencyProperty.Register(
        nameof(HighlightBackground),
        typeof(Brush),
        typeof(HighlightTextBlock),
        new PropertyMetadata(null, OnTextPartChanged));

    public static readonly DependencyProperty HighlightForegroundProperty = DependencyProperty.Register(
        nameof(HighlightForeground),
        typeof(Brush),
        typeof(HighlightTextBlock),
        new PropertyMetadata(null, OnTextPartChanged));

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string HighlightText
    {
        get => (string)GetValue(HighlightTextProperty);
        set => SetValue(HighlightTextProperty, value);
    }

    public Brush? HighlightBackground
    {
        get => (Brush?)GetValue(HighlightBackgroundProperty);
        set => SetValue(HighlightBackgroundProperty, value);
    }

    public Brush? HighlightForeground
    {
        get => (Brush?)GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    private static void OnTextPartChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ((HighlightTextBlock)dependencyObject).RefreshInlines();
    }

    private void RefreshInlines()
    {
        Inlines.Clear();

        var source = SourceText ?? string.Empty;
        var keyword = HighlightText ?? string.Empty;
        if (source.Length == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(keyword))
        {
            Inlines.Add(new Run(source));
            return;
        }

        var current = 0;
        while (current < source.Length)
        {
            var matchIndex = source.IndexOf(keyword, current, StringComparison.CurrentCultureIgnoreCase);
            if (matchIndex < 0)
            {
                Inlines.Add(new Run(source[current..]));
                break;
            }

            if (matchIndex > current)
            {
                Inlines.Add(new Run(source[current..matchIndex]));
            }

            var matchLength = Math.Min(keyword.Length, source.Length - matchIndex);
            Inlines.Add(new Run(source.Substring(matchIndex, matchLength))
            {
                Background = HighlightBackground,
                Foreground = HighlightForeground ?? Foreground,
                FontWeight = FontWeights.SemiBold
            });

            current = matchIndex + Math.Max(1, matchLength);
        }
    }
}
