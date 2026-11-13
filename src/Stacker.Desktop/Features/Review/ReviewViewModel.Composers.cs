using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Desktop.ViewModels;

public partial class ReviewViewModel
{
    partial void OnFileComposerChanged(string value)
    {
        if (!_loading && !_switchingFile && SelectedReviewFile is { } path)
        {
            _draft.FileComposers[path] = value;
            QueueSave();
        }
    }
    partial void OnSelectedReviewFileChanged(string? value)
    {
        _switchingFile = true;
        FileComposer = value is not null && _draft.FileComposers.TryGetValue(value, out var text) ? text : "";
        _switchingFile = false;
        UpdateFileThreads();
    }
    internal void UpdateFileThreads()
    {
        Sync(FileThreads, Threads.Where(t => t.IsFileLevel && t.Path == SelectedReviewFile).ToArray(), t => t.Id);
        OnPropertyChanged(nameof(FileThreadsLabel));
        OnPropertyChanged(nameof(CanPostFile));
    }

    /// <summary>Creates a line or contiguous multi-line selection tied to this exact PR diff snapshot.</summary>
    public void SetSelection(IEnumerable<RenderedDiffLine> selected, string? side = null)
    {
        if (IsBusy)
            return;
        CloseInlineEditors();
        var lines = selected.ToArray();
        _selection = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanSuggestCodeChange));
        OnPropertyChanged(nameof(SelectedCode));
        if (!_section.IsPrSnapshot || lines.Length == 0 || _section.SelectedFile is not { } file)
        {
            OnPropertyChanged(nameof(SelectionLabel));
            return;
        }
        if (lines.Any(line => line.IsExpandedContext))
        {
            Message = "Select lines from the original diff to comment.";
            NotifyPublicationState();
            return;
        }
        if (!ReviewSelection.TryCreateAnchor(PullRequest, file.NewPath, lines.Select(l => l.Source).ToArray(), side, out _selection, out var error))
        {
            Message = error!;
            NotifyPublicationState();
            return;
        }
        SelectComposer("line:" + JsonSerializer.Serialize(_selection));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanSuggestCodeChange));
        OnPropertyChanged(nameof(SelectedCode));
        NotifyPublicationState();
    }
    /// <summary>Prefills a standard GitHub suggestion with the selected head-side code, retaining existing prose.</summary>
    [RelayCommand]
    internal void SuggestCodeChange()
    {
        if (!CanSuggestCodeChange)
        {
            Message = "Select current code on the right side to suggest a replacement. Deleted lines support comments only.";
            return;
        }
        if (HasSuggestionPreview)
        {
            Message = "Edit the existing suggestion block to change the replacement.";
            return;
        }
        var suggestion = ReviewContent.Suggestion(SelectedCode!);
        Composer = string.IsNullOrWhiteSpace(Composer) ? suggestion : Composer + "\n\n" + suggestion;
        Message = "Edit the code inside the suggestion block, then add it to your review or post it.";
    }
    internal string? GetSelectedCode()
    {
        if (_selection is not { Side: "RIGHT" } anchor || anchor.HeadSha != _section.Result.HeadSha || anchor.BaseSha != PullRequest.BaseSha || anchor.Path != _section.SelectedFile?.NewPath)
            return null;
        var start = anchor.StartLine ?? anchor.Line;
        var lines = _section.Lines.Where(l => l.Kind is DiffLineKind.Added or DiffLineKind.Context && l.NewLineNumber >= start && l.NewLineNumber <= anchor.Line).OrderBy(l => l.NewLineNumber).ToArray();
        return lines.Length == anchor.Line - start + 1 ? string.Join("\n", lines.Select(l => l.Text.TrimEnd('\r'))) : null;
    }
    [RelayCommand]
    internal async Task AddToDraftAsync()
    {
        if (IsBusy)
            return;
        if (_selection is null || string.IsNullOrWhiteSpace(Composer))
        {
            Message = "Select code lines and enter a comment.";
            return;
        }
        if (_selection.Side != "RIGHT" && HasSuggestionPreview)
        {
            Message = "Suggestions can only replace right-side code. Keep a normal comment on deleted lines.";
            return;
        }
        if (_draft.HeadSha != _selection.HeadSha || _draft.BaseSha != _selection.BaseSha)
        {
            Message = "This draft belongs to another PR version. Recheck its existing anchors first.";
            return;
        }
        var comment = new DraftComment(_selection, Composer);
        _draft.Comments.Add(comment);
        DraftComments.Add(comment);
        Composer = "";
        QueueSave();
        await _saveQueue;
        if (_saveError is null)
            Message = "Line comment saved to the local draft.";
    }
    [RelayCommand]
    internal void RemoveDraftComment(DraftComment comment)
    {
        if (IsBusy)
            return;
        _draft.Comments.Remove(comment);
        DraftComments.Remove(comment);
        QueueSave();
    }
    [RelayCommand]
    internal Task UseCurrentVersionAsync()
=> _reviewRecoveryController.UseCurrentVersionAsync();
    [RelayCommand] internal Task PostCommentAsync() => _reviewPublicationController.PostCommentAsync();
    [RelayCommand] internal Task PostFileCommentAsync() => _reviewPublicationController.PostFileCommentAsync();
    [RelayCommand] internal Task PostInlineAsync() => _reviewPublicationController.PostInlineAsync();
    [RelayCommand] internal Task ReplyAsync() => _reviewPublicationController.ReplyAsync();
    [RelayCommand] internal Task SubmitReviewAsync() => _reviewPublicationController.SubmitReviewAsync();
    internal Task VerifyAsync(IReadOnlyList<ReviewAnchor> anchors) => _reviewPublicationController.VerifyAsync(anchors);
    // Publication first persists a pending marker, then revalidates the server snapshot; a failed write keeps the draft recoverable.
    internal Task PublishAsync(string kind, IReadOnlyList<ReviewAnchor> anchors, Func<string, Task> send, string? filePath = null) => _reviewPublicationController.PublishAsync(kind, anchors, send, filePath);
    [RelayCommand] internal Task ToggleResolvedAsync() => _reviewPublicationController.ToggleResolvedAsync();
    [RelayCommand] internal Task UnlockRetryAsync() => _reviewPublicationController.UnlockRetryAsync();
    internal static string Mark(string body, string marker) => body + "\n\n<!-- stacker:" + marker + " -->";
    // Serialize saves so an older asynchronous write cannot overwrite newer text typed by the user.
    internal void QueueSave() => _reviewDraftController.QueueSave();
    internal Task SaveOrderedAsync(Task previous, ReviewDraft copy) => _reviewDraftController.SaveOrderedAsync(previous, copy);
    /// <summary>Waits for queued draft writes so closing or switching context cannot silently lose edits.</summary>
}
