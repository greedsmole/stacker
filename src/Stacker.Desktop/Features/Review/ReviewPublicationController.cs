using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed class ReviewPublicationController(ReviewViewModel owner)
{
    public void ClearPublishedComposer()
    {
        // A reload can reconcile a send before the send itself returns; without an owned key there is nothing to clear.
        var key = owner._draft.PendingComposerKey;
        if (key is null) { owner._draft.PendingBody = null; return; }
        if (owner._draft.ContextComposers.GetValueOrDefault(key) == owner._draft.PendingBody || owner._draft.PendingBody is null)
        { owner._draft.ContextComposers[key] = ""; if (key == "pr") owner._draft.Composer = ""; if (key == owner._composerKey.Encoded) owner.Composer = ""; }
        owner._draft.PendingComposerKey = null; owner._draft.PendingBody = null;
    }

    public async Task PostCommentAsync()
    { var body = owner.Composer; await PublishAsync("comment", [], marker => Boundary.UnwrapAsync(owner._writer.CommentAsync(owner.Context, owner.PullRequest.Number, ReviewViewModel.Mark(body, marker)))); }

    public async Task PostFileCommentAsync()
    {
        if (owner.PublicationBlockReason is { } blocked) { owner.Message = blocked; return; }
        owner.PublicationProblem = null;
        if (!owner.CanPostFile) { owner.Message = "Select a current PR file before commenting. Reload PR changes if it is no longer available."; return; }
        if (owner._draft.HeadSha != owner.PullRequest.HeadSha || owner._draft.BaseSha != owner.PullRequest.BaseSha) { owner.Message = "File draft belongs to an older PR version. Recheck it before adopting the current version."; return; }
        var path = owner.SelectedReviewFile!; var body = owner.FileComposer;
        await PublishAsync("file", [], marker => Boundary.UnwrapAsync(owner._writer.FileCommentAsync(owner.Context, owner.PullRequest, path, ReviewViewModel.Mark(body, marker))), path);
    }

    public async Task PostInlineAsync()
    {
        if (owner.PublicationBlockReason is { } blocked) { owner.Message = blocked; return; }
        owner.PublicationProblem = null;
        if (owner._selection is null) { owner.Message = "Select code lines first."; return; }
        if (owner._selection.Side != "RIGHT" && owner.HasSuggestionPreview) { owner.Message = "Suggestions can only replace right-side code. Keep a normal comment on deleted lines."; return; }
        var anchor = owner._selection; var body = owner.Composer;
        await PublishAsync("inline", [anchor], marker => Boundary.UnwrapAsync(owner._writer.InlineCommentAsync(owner.Context, new(anchor, ReviewViewModel.Mark(body, marker)))));
    }

    public async Task ReplyAsync()
    {
        if (owner.SelectedThread?.Comments.FirstOrDefault() is not { } first) { owner.Message = "Select a thread to reply to."; return; }
        var body = owner.Composer;
        await PublishAsync("reply", [], marker => Boundary.UnwrapAsync(owner._writer.ReplyAsync(owner.Context, owner.PullRequest.Number, first.Id, ReviewViewModel.Mark(body, marker))));
    }

    public async Task SubmitReviewAsync()
    {
        var copy = JsonSerializer.Deserialize<ReviewDraft>(JsonSerializer.Serialize(owner._draft))!;
        await PublishAsync("review", copy.Comments.Select(c => c.Anchor).ToArray(), async marker =>
        {
            copy.Summary = ReviewViewModel.Mark(copy.Summary, marker);
            (await owner._writer.SubmitReviewAsync(owner.Context, copy)).OrThrow();
        });
    }

    public Task VerifyAsync(IReadOnlyList<ReviewAnchor> anchors) => owner.Session.ValidateAsync(owner.Context, owner.PullRequest, anchors);

    public async Task PublishAsync(string kind, IReadOnlyList<ReviewAnchor> anchors, Func<string, Task> send, string? filePath = null)
    {
        if (owner.PublicationBlockReason is { } blocked) { owner.Message = blocked; return; }
        owner.PublicationProblem = null;
        if (kind != "review" && string.IsNullOrWhiteSpace(kind == "file" ? owner.FileComposer : owner.Composer)) { owner.Message = "Enter a comment first."; return; }
        if (kind == "review" && (owner._draft.HeadSha != owner.PullRequest.HeadSha || owner._draft.BaseSha != owner.PullRequest.BaseSha)) { owner.Message = "Draft version differs from this PR snapshot."; return; }
        if (kind == "review" && owner._draft.Comments.Any(c => c.Anchor.Side != "RIGHT" && ReviewContent.Parse(c.Body).Any(b => b.IsSuggestion))) { owner.Message = "A suggestion targets deleted lines. Edit it into a normal comment before submitting."; return; }
        if (kind == "review" && owner.Decision == "REQUEST_CHANGES" && string.IsNullOrWhiteSpace(owner.Summary)) { owner.Message = "Explain the requested changes in the review summary."; return; }
        if (kind == "review" && owner.Decision != "APPROVE" && string.IsNullOrWhiteSpace(owner.Summary) && owner.DraftComments.Count == 0) { owner.Message = "Add a summary or line comments."; return; }
        BeginPublication("Publishing to GitHub…");
        var dispatchStarted = false;
        var confirmed = false;
        try
        {
            await VerifyAsync(anchors);
            if (filePath is not null) (await owner._reader.ValidateFileAsync(owner.Context, owner.PullRequest, filePath)).OrThrow();
            var marker = Guid.NewGuid().ToString("N"); owner._draft.PendingOperation = PendingOperation.For(kind, marker).Encode(); owner._draft.PendingFilePath = filePath; owner._draft.PendingComposerKey = owner._composerKey.Encoded; owner._draft.PendingBody = kind == "file" ? owner.FileComposer : owner.Composer;
            if (kind == "review") { owner._draft.PendingSummary = owner.Summary; owner._draft.PendingReviewComments = owner._draft.Comments.ToList(); }
            ReviewSessionService.Prepare(owner._draft, marker, kind, owner.Context, owner.PullRequest);
            owner.QueueSave(); await owner.FlushAsync();
            ReviewSessionService.Transition(owner._draft, PublicationPhase.Sending);
            owner.QueueSave(); await owner.FlushAsync();
            dispatchStarted = true;
            using (OperationCorrelation.Enter(marker)) await send(marker);
            confirmed = true;
            ReviewSessionService.Transition(owner._draft, PublicationPhase.Confirmed);
            owner._draft.PendingOperation = null; owner._draft.PendingFilePath = null;
            if (kind == "review") ClearSubmittedDraft(); else if (kind == "file" && filePath is not null) ClearPublishedFile(filePath); else ClearPublishedComposer();
            owner.QueueSave(); await owner.FlushAsync();
            owner.Message = kind == "review" && anchors.Count > 0
                ? $"Published the review with {anchors.Count} line {(anchors.Count == 1 ? "comment" : "comments")}."
                : kind == "review" ? "Review published." : "Comment published.";
        }
        catch (Exception ex) { ReportFailure(ex, dispatchStarted, confirmed); }
        finally { owner.IsBusy = false; }
        if (confirmed) _ = owner.ReloadAfterPublicationAsync();
    }

    public async Task ToggleResolvedAsync()
    {
        if (owner.PublicationBlockReason is { } blocked) { owner.Message = blocked; return; }
        if (owner.SelectedThread is not { CanResolve: true } thread) return;
        BeginPublication(thread.IsResolved ? "Reopening thread on GitHub…" : "Resolving thread on GitHub…");
        var dispatchStarted = false;
        var confirmed = false;
        try
        {
            await VerifyAsync([]); owner._draft.PendingOperation = PendingOperation.ForResolve(thread.Id, !thread.IsResolved).Encode(); ReviewSessionService.Prepare(owner._draft, Guid.NewGuid().ToString("N"), "resolve", owner.Context, owner.PullRequest);
            owner.QueueSave(); await owner.FlushAsync();
            ReviewSessionService.Transition(owner._draft, PublicationPhase.Sending); owner.QueueSave(); await owner.FlushAsync();
            dispatchStarted = true;
            using (OperationCorrelation.Enter(owner._draft.Operation!.Id))
                (await owner._writer.ResolveAsync(owner.Context, thread.Id, !thread.IsResolved)).OrThrow();
            confirmed = true;
            ReviewSessionService.Transition(owner._draft, PublicationPhase.Confirmed);
            owner._draft.PendingOperation = null; owner.QueueSave(); await owner.FlushAsync();
            owner.Message = thread.IsResolved ? "Thread reopened." : "Thread resolved.";
            await owner.ReloadAfterPublicationAsync();
        }
        catch (Exception ex) { ReportFailure(ex, dispatchStarted, confirmed); }
        finally { owner.IsBusy = false; }
    }

    public async Task UnlockRetryAsync() { if (owner.IsBusy) return; ReviewSessionService.Transition(owner._draft, PublicationPhase.Rejected); owner._draft.PendingOperation = null; owner._draft.PendingFilePath = null; owner.QueueSave(); await owner._saveQueue; owner.NotifyPublicationState(); if (owner._saveError is null) { owner.PublicationProblem = null; owner.Message = "Retry unlocked after your manual GitHub check."; } }

    public void ClearPublishedFile(string path)
    {
        if (owner._draft.PendingBody is null || owner._draft.FileComposers.GetValueOrDefault(path) == owner._draft.PendingBody)
        { owner._draft.FileComposers[path] = ""; if (owner.SelectedReviewFile == path) owner.FileComposer = ""; }
        owner._draft.PendingBody = null;
    }

    public void ClearSubmittedDraft()
    {
        if (owner._draft.PendingReviewComments is null) return; // Older uncertain operations have no safe snapshot to remove.
        if (owner.Summary == owner._draft.PendingSummary) { owner._loading = true; owner.Summary = ""; owner._loading = false; owner._draft.Summary = ""; }
        foreach (var sent in owner._draft.PendingReviewComments)
            foreach (var match in owner._draft.Comments.Where(c => c.Id == sent.Id && c == sent).ToArray()) { owner._draft.Comments.Remove(match); owner.DraftComments.Remove(match); }
        owner._draft.PendingReviewComments = null; owner._draft.PendingSummary = null;
    }

    private void BeginPublication(string status)
    {
        owner._discussionLoad?.Cancel();
        owner.PublicationProblem = null;
        owner.IsBusy = true;
        owner.Message = status;
    }

    private void ReportFailure(Exception error, bool dispatchStarted, bool confirmed)
    {
        var detail = error.Message;
        if (confirmed)
        {
            detail = "GitHub confirmed the action. Could not save the local confirmation. " + detail;
        }
        else if (!dispatchStarted || error is StackerException { Problem.Certainty: EffectCertainty.NotStarted or EffectCertainty.Rejected })
        {
            if (owner._draft.PendingOperation is not null)
            {
                ReviewSessionService.Transition(owner._draft, PublicationPhase.Rejected);
                owner._draft.PendingOperation = null;
                owner._draft.PendingFilePath = null;
                owner.QueueSave();
            }
        }
        else if (owner._draft.PendingOperation is not null)
        {
            ReviewSessionService.Transition(owner._draft, PublicationPhase.Uncertain);
            owner.QueueSave();
            detail += " GitHub may have received this action. Check publication status before retrying.";
        }
        owner.PublicationProblem = detail;
        owner.Message = detail;
    }
}
