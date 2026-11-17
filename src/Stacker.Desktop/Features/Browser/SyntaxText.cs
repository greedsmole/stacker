// Purpose: Selectable text control that renders syntax spans as Avalonia runs while preserving the original selectable text.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;

/// <summary>Renders syntax-highlighted tokens while retaining selectable text behavior.</summary>
/// <remarks>Token positions are clamped to the source text so malformed grammar output cannot produce invalid slices.</remarks>
public sealed class SyntaxText : SelectableTextBlock
{
    /// <summary>Avalonia property containing the rendered diff line.</summary>
    public static readonly StyledProperty<RenderedDiffLine?> LineProperty = AvaloniaProperty.Register<SyntaxText, RenderedDiffLine?>(nameof(Line));
    /// <summary>The current syntax-tokenized line, or null when empty.</summary>
    public RenderedDiffLine? Line
    {
        get => GetValue(LineProperty); set => SetValue(LineProperty, value);
    }
    /// <summary>Maintains the line subscription and redraws when the bound line changes.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty)
        {
            if (change.OldValue is RenderedDiffLine old)
                old.PropertyChanged -= LineChanged;
            if (Line is { } line)
                line.PropertyChanged += LineChanged;
            Render();
        }
    }
    /// <summary>Releases the line subscription when detached.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Line is { } line)
            line.PropertyChanged -= LineChanged;
        base.OnDetachedFromVisualTree(e);
    }
    /// <summary>Reattaches the line subscription and renders the current value.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Line is { } line)
        {
            line.PropertyChanged -= LineChanged;
            line.PropertyChanged += LineChanged;
        }
        Render();
    }
    /// <summary>Redraws only when token spans change.</summary>
    private void LineChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RenderedDiffLine.Tokens))
            Render();
    }
    /// <summary>Rebuilds inline runs from source text and clamped token spans.</summary>
    private void Render()
    {
        Inlines?.Clear();
        if (Line is null)
            return;
        // Git splits patches on LF, leaving the CR of a CRLF line ending in the raw row.
        var text = Line.Text;
        if (text.EndsWith('\r'))
            text = text[..^1];
        var cursor = 0;
        foreach (var span in Line.Tokens.OrderBy(s => s.Start))
        {
            var start = Math.Clamp(span.Start, cursor, text.Length);
            var length = Math.Clamp(span.Length, 0, text.Length - start);
            if (start > cursor)
                Inlines!.Add(new Run(text[cursor..start]));
            if (length > 0)
            {
                var run = new Run(text.Substring(start, length));
                if (BrushFor(span.Color) is { } brush)
                    run.Foreground = brush;
                Inlines!.Add(run);
            }
            cursor = start + length;
        }
        if (cursor < text.Length)
            Inlines!.Add(new Run(text[cursor..]));
    }
    // Themes come from a grammar bundle, so a colour is data: parse it defensively and reuse the brush.
    /// <summary>Brushes keyed by grammar color text to avoid one allocation per span.</summary>
    private static readonly Dictionary<string, IBrush?> BrushCache = new(StringComparer.Ordinal);
    /// <summary>Parses and caches a grammar color, using the fallback for invalid input.</summary>
    /// <param name="colour">Color text emitted by the syntax grammar.</param>
    private static IBrush? BrushFor(string colour)
    {
        lock (BrushCache)
        {
            if (BrushCache.TryGetValue(colour, out var cached))
                return cached;
            var brush = Color.TryParse(colour, out var parsed) ? new SolidColorBrush(parsed) : null;
            BrushCache[colour] = brush;
            return brush;
        }
    }
}
