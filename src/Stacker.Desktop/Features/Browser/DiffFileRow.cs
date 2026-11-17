using System.Collections.ObjectModel;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class DiffFileRow(FileChange change) : ObservableObject
{
    public FileChange Change { get; } = change;
    public string AccessibleName => $"{Change.DisplayPath} · {Change.Kind}";
    public bool IsReviewed => ReviewStatus?.State == FileReviewState.Reviewed;
    public bool HasReviewStatus => ReviewStatus is not null;
    public bool CanChangeReview => HasReviewStatus && !SavingReview;
    public string ReviewAction => ReviewStatus?.State switch
    {
        FileReviewState.Reviewed => "Mark unreviewed",
        FileReviewState.CodeChanged => "Code changed since review. Mark reviewed",
        FileReviewState.BaseChanged => "Base changed since review. Mark reviewed",
        FileReviewState.MetadataChanged => "File metadata changed since review. Mark reviewed",
        FileReviewState.Unavailable => "Previous review could not be compared. Mark reviewed",
        _ => "Mark reviewed"
    };

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsReviewed), nameof(HasReviewStatus), nameof(CanChangeReview), nameof(ReviewAction))]
    private FileReviewStatus? _reviewStatus;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChangeReview))]
    private bool _savingReview;
}
