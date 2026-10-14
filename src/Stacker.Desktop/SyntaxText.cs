// Purpose: Selectable text control that renders syntax spans as Avalonia runs while preserving the original selectable text.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Stacker.Desktop.ViewModels;
namespace Stacker.Desktop;

public sealed class SyntaxText : SelectableTextBlock
{
    public static readonly StyledProperty<RenderedDiffLine?> LineProperty = AvaloniaProperty.Register<SyntaxText, RenderedDiffLine?>(nameof(Line));
    public RenderedDiffLine? Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty)
        {
            if (change.OldValue is RenderedDiffLine old) old.PropertyChanged -= LineChanged;
            if (Line is { } line) line.PropertyChanged += LineChanged;
            Render();
        }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { if (Line is { } line) line.PropertyChanged -= LineChanged; base.OnDetachedFromVisualTree(e); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); if (Line is { } line) { line.PropertyChanged -= LineChanged; line.PropertyChanged += LineChanged; } Render(); }
    private void LineChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(RenderedDiffLine.Tokens)) Render(); }
    private void Render()
    {
        Inlines?.Clear(); if (Line is null) return;
        var text = Line.Text; var cursor = 0;
        foreach (var span in Line.Tokens.OrderBy(s => s.Start))
        {
            var start = Math.Clamp(span.Start, cursor, text.Length); var length = Math.Clamp(span.Length, 0, text.Length - start);
            if (start > cursor) Inlines!.Add(new Run(text[cursor..start]));
            if (length > 0) Inlines!.Add(new Run(text.Substring(start, length)) { Foreground = BrushFor(span.Color) });
            cursor = start + length;
        }
        if (cursor < text.Length) Inlines!.Add(new Run(text[cursor..]));
    }
    // Themes come from a grammar bundle, so a colour is data: parse it defensively and reuse the brush.
    private static readonly Dictionary<string, IBrush> BrushCache = new(StringComparer.Ordinal);
    private static readonly IBrush FallbackBrush = new SolidColorBrush(Colors.Transparent);
    private static IBrush BrushFor(string colour)
    {
        lock (BrushCache)
        {
            if (BrushCache.TryGetValue(colour, out var cached)) return cached;
            var brush = Color.TryParse(colour, out var parsed) ? new SolidColorBrush(parsed) : FallbackBrush;
            BrushCache[colour] = brush;
            return brush;
        }
    }
}
