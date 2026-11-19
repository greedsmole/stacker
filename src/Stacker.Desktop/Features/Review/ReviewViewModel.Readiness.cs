// Purpose: Review publication readiness and the UI notifications that keep posting controls current.
using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public partial class ReviewViewModel
{
    public bool CanPublish => PublicationBlockReason is null;
    internal string? PublicationBlockReason => IsDemo ? "Demo is offline. Publishing is disabled."
        : !_draftLoaded ? "Load the local review draft before publishing."
        : IsBusy ? "A review action is in progress."
        : HasPendingPublication ? "Check the previous publication on GitHub before sending again."
        : null;
    public string ReviewStatus => IsBusy ? Message
        : PublicationProblem ?? (string.IsNullOrWhiteSpace(Message) ? PublicationBlockReason ?? "" : Message);
    public bool HasReviewStatus => ReviewStatus.Length > 0;
    public bool HasPublicationProblem => PublicationProblem is not null;
    public bool CanPostFile => CanPublish && SelectedReviewFile is not null && _reviewableFiles.Contains(SelectedReviewFile);
    public bool CanPostComment => CanPublish && !string.IsNullOrWhiteSpace(Composer);
    public bool CanPostFileComment => CanPostFile && !string.IsNullOrWhiteSpace(FileComposer);
    public bool CanReply => CanPostComment && SelectedThread?.Comments.FirstOrDefault() is not null;
    public bool CanSubmitReview => CanPublish && (Decision switch
    {
        "APPROVE" => true,
        "REQUEST_CHANGES" => !string.IsNullOrWhiteSpace(Summary),
        _ => !string.IsNullOrWhiteSpace(Summary) || DraftComments.Count > 0
    });

    internal void NotifyPublicationState()
    {
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(CanPostFile));
        OnPropertyChanged(nameof(CanPostComment));
        OnPropertyChanged(nameof(CanPostFileComment));
        OnPropertyChanged(nameof(CanReply));
        OnPropertyChanged(nameof(CanSubmitReview));
        OnPropertyChanged(nameof(HasPendingPublication));
        OnPropertyChanged(nameof(HasStaleDraft));
        OnPropertyChanged(nameof(IsInitialized));
        OnPropertyChanged(nameof(CanEditDraft));
        OnPropertyChanged(nameof(CanSuggestCodeChange));
        OnPropertyChanged(nameof(SelectedCode));
        OnPropertyChanged(nameof(ReviewStatus));
        OnPropertyChanged(nameof(HasReviewStatus));
    }
    partial void OnIsBusyChanged(bool value) => NotifyPublicationState();
    partial void OnSelectedThreadChanged(ReviewThread? value) => OnPropertyChanged(nameof(CanReply));
}
