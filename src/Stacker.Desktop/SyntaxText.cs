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
            if (length > 0) Inlines!.Add(new Run(text.Substring(start, length)) { Foreground = Brush.Parse(span.Color) });
            cursor = start + length;
        }
        if (cursor < text.Length) Inlines!.Add(new Run(text[cursor..]));
    }
}
