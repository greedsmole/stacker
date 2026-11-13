using System.Collections.ObjectModel;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class RenderedDiffLine(DiffLine line) : ObservableObject
{
    public DiffLine Source { get; } = line;
    public DiffLineKind Kind => Source.Kind;
    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsHunk => Kind == DiffLineKind.Header;
    public int? OldLineNumber => Source.OldLineNumber;
    public int? NewLineNumber => Source.NewLineNumber;
    public bool IsCode => Kind is DiffLineKind.Context or DiffLineKind.Added or DiffLineKind.Removed;
    public bool IsExpandedContext
    {
        get; init;
    }
    public bool CanComment => IsCode && AllowComment && !IsExpandedContext;
    public bool CanCommentLeft => IsRemoved && CanComment;
    public bool IsLineCommentVisible => CanComment && !IsRangeSelected;
    public bool AllowComment { get; init; } = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCollapsedContext), nameof(ContextLabel), nameof(ShowExpandUp))]
    private DiffContextGap? _contextGap;
    public bool IsCollapsedContext => ContextGap is not null;
    public bool ShowExpandUp => ContextGap is { HasHunkAbove: true, HasHunkBelow: true, Count: > DiffContextGap.ExpandStep };
    public string? ContextLabel => ContextGap switch
    {
        null => null,
        { Count: 1 } => "Show 1 unchanged line",
        { Count: <= DiffContextGap.ExpandStep } gap => $"Show {gap.Count} unchanged lines",
        { RevealFromEnd: true } gap => $"Show {gap.RevealCount} lines above · {gap.Count} hidden",
        { } gap => $"Show {gap.RevealCount} lines below · {gap.Count} hidden"
    };
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLineCommentVisible))] private bool _isRangeSelected;
    public string Marker => Kind == DiffLineKind.Added ? "+" : Kind == DiffLineKind.Removed ? "−" : "";
    public string Text => IsCode && Source.Text.Length > 0 ? Source.Text[1..] : Source.Text;
    [ObservableProperty] private ReviewViewModel? _editor;
    public ObservableCollection<ReviewThread> InlineThreads { get; } = [];
    public ObservableCollection<DraftComment> Drafts { get; } = [];
    public bool HasEditor => Editor is not null;
    partial void OnEditorChanged(ReviewViewModel? value) => OnPropertyChanged(nameof(HasEditor));
    [ObservableProperty] private IReadOnlyList<SyntaxSpan> _tokens = [];
    [ObservableProperty] private TextWrapping _textWrapping = TextWrapping.NoWrap;
}
