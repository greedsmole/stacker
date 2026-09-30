using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Core;

namespace Stacker.Desktop;

/// <summary>Renders review text and suggestion fences as ordered, selectable replacement previews.</summary>
public partial class ReviewBodyView : UserControl
{
    public static readonly StyledProperty<string> BodyProperty = AvaloniaProperty.Register<ReviewBodyView, string>(nameof(Body), "");
    public static readonly StyledProperty<string?> OriginalCodeProperty = AvaloniaProperty.Register<ReviewBodyView, string?>(nameof(OriginalCode));
    public static readonly StyledProperty<bool> HasOriginalCodeProperty = AvaloniaProperty.Register<ReviewBodyView, bool>(nameof(HasOriginalCode));
    public string Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public string? OriginalCode { get => GetValue(OriginalCodeProperty); set => SetValue(OriginalCodeProperty, value); }
    public bool HasOriginalCode => GetValue(HasOriginalCodeProperty);

    public ReviewBodyView() { InitializeComponent(); Render(); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BodyProperty && BodyBlocks is not null) Render();
        if (change.Property == OriginalCodeProperty && BodyBlocks is not null) Render();
    }
    private void Render()
    {
        var blocks = ReviewContent.Parse(Body ?? "").Where(b => b.IsSuggestion || !string.IsNullOrWhiteSpace(b.Text)).ToArray();
        var suggestions = blocks.Where(b => b.IsSuggestion).ToArray();
        // An offset suggestion or multiple replacement blocks must not borrow a misleading original-code preview.
        SetValue(HasOriginalCodeProperty, OriginalCode is not null && suggestions.Length == 1 && suggestions[0].LinesAbove == 0 && suggestions[0].LinesBelow == 0);
        BodyBlocks.ItemsSource = blocks;
    }
    private async void CopySuggestionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReviewContentBlock block } button || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(block.Text); button.Content = "Copied"; }
        catch { button.Content = "Copy failed"; }
    }
}
