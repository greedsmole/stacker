using System.Text.Json;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class ReviewPublicationController(ReviewViewModel owner)
{

    public async Task PostCommentAsync()
    {
        var body = owner.Composer;
        await PublishAsync("comment", [], marker => Boundary.UnwrapAsync(owner._writer.CommentAsync(owner.Context, owner.PullRequest.Number, ReviewViewModel.Mark(body, marker))));
    }

    public async Task PostFileCommentAsync()
    {
        if (owner.PublicationBlockReason is { } blocked)
        {
            owner.Message = blocked;
            return;
        }
        owner.PublicationProblem = null;
        if (!owner.CanPostFile)
        {
            owner.Message = "Select a current PR file before commenting. Reload PR changes if it is no longer available.";
            return;
        }
        if (owner._draft.HeadSha != owner.PullRequest.HeadSha || owner._draft.BaseSha != owner.PullRequest.BaseSha)
        {
            owner.Message = "File draft belongs to an older PR version. Recheck it before adopting the current version.";
            return;
        }
        var path = owner.SelectedReviewFile!;
        var body = owner.FileComposer;
        await PublishAsync("file", [], marker => Boundary.UnwrapAsync(owner._writer.FileCommentAsync(owner.Context, owner.PullRequest, path, ReviewViewModel.Mark(body, marker))), path);
    }

    public async Task PostInlineAsync()
    {
        if (owner.PublicationBlockReason is { } blocked)
        {
            owner.Message = blocked;
            return;
        }
        owner.PublicationProblem = null;
        if (owner._selection is null)
        {
            owner.Message = "Select code lines first.";
            return;
        }
        if (owner._selection.Side != "RIGHT" && owner.HasSuggestionPreview)
        {
            owner.Message = "Suggestions can only replace right-side code. Keep a normal comment on deleted lines.";
            return;
        }
        var anchor = owner._selection;
        var body = owner.Composer;
        await PublishAsync("inline", [anchor], marker => Boundary.UnwrapAsync(owner._writer.InlineCommentAsync(owner.Context, new(anchor, ReviewViewModel.Mark(body, marker)))));
    }

    public async Task ReplyAsync()
    {
        if (owner.SelectedThread?.Comments.FirstOrDefault() is not { } first)
        {
            owner.Message = "Select a thread to reply to.";
            return;
        }
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
        if (owner.PublicationBlockReason is { } blocked)
        {
            owner.Message = blocked;
            return;
        }
        owner.PublicationProblem = null;
        if (kind != "review" && string.IsNullOrWhiteSpace(kind == "file" ? owner.FileComposer : owner.Composer))
        {
            owner.Message = "Enter a comment first.";
            return;
        }
        if (kind == "review" && (owner._draft.HeadSha != owner.PullRequest.HeadSha || owner._draft.BaseSha != owner.PullRequest.BaseSha))
        {
            owner.Message = "Draft version differs from this PR snapshot.";
            return;
        }
        if (kind == "review" && owner._draft.Comments.Any(c => c.Anchor.Side != "RIGHT" && ReviewContent.Parse(c.Body).Any(b => b.IsSuggestion)))
        {
            owner.Message = "A suggestion targets deleted lines. Edit it into a normal comment before submitting.";
            return;
        }
        if (kind == "review" && owner.Decision == "REQUEST_CHANGES" && string.IsNullOrWhiteSpace(owner.Summary))
        {
            owner.Message = "Explain the requested changes in the review summary.";
            return;
        }
        if (kind == "review" && owner.Decision != "APPROVE" && string.IsNullOrWhiteSpace(owner.Summary) && owner.DraftComments.Count == 0)
        {
            owner.Message = "Add a summary or line comments.";
            return;
        }
        var success = kind == "review" && anchors.Count > 0
            ? $"Published the review with {anchors.Count} line {(anchors.Count == 1 ? "comment" : "comments")}."
            : kind == "review" ? "Review published." : "Comment published.";
        await RunPublicationAsync(kind, anchors, send, success, filePath);
    }

    public async Task ToggleResolvedAsync()
    {
        if (owner.PublicationBlockReason is { } blocked)
        {
            owner.Message = blocked;
            return;
        }
        if (owner.SelectedThread is not { CanResolve: true } thread)
            return;
        var resolved = !thread.IsResolved;
        await RunPublicationAsync("resolve", [], _ => Boundary.UnwrapAsync(owner._writer.ResolveAsync(owner.Context, thread.Id, resolved)),
            resolved ? "Thread resolved." : "Thread reopened.", resolution: PendingOperation.ForResolve(thread.Id, resolved));
    }

    private async Task RunPublicationAsync(string kind, IReadOnlyList<ReviewAnchor> anchors, Func<string, Task> send,
        string success, string? filePath = null, PendingOperation? resolution = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var pending = resolution ?? PendingOperation.For(kind, id);
        var marker = pending.Encode();
        var composerKey = owner._composerKey.Encoded;
        var body = pending.Kind == PendingKind.FileComment ? owner.FileComposer : owner.Composer;
        var summary = pending.Kind == PendingKind.Review ? owner.Summary : null;
        var comments = pending.Kind == PendingKind.Review ? owner._draft.Comments.ToList() : null;
        owner._discussionLoad?.Cancel();
        owner.PublicationProblem = null;
        owner.IsBusy = true;
        owner.Message = resolution switch
        {
            { Resolved: true } => "Resolving thread on GitHub…",
            { Resolved: false } => "Reopening thread on GitHub…",
            _ => "Publishing to GitHub…"
        };
        var phase = AttemptPhase.BeforeDispatch;
        try
        {
            await VerifyAsync(anchors);
            if (filePath is not null)
                (await owner._reader.ValidateFileAsync(owner.Context, owner.PullRequest, filePath)).OrThrow();
            owner._draft.PendingOperation = marker;
            owner._draft.PendingFilePath = filePath;
            owner._draft.PendingComposerKey = resolution is null ? composerKey : null;
            owner._draft.PendingBody = resolution is null ? body : null;
            owner._draft.PendingSummary = summary;
            owner._draft.PendingReviewComments = comments;
            ReviewSessionService.Prepare(owner._draft, id, kind, owner.Context, owner.PullRequest);
            owner.QueueSave();
            await owner.FlushAsync();
            ReviewSessionService.Transition(owner._draft, PublicationPhase.Sending);
            owner.QueueSave();
            await owner.FlushAsync();

            phase = AttemptPhase.Dispatching;
            string? executionProblem = null;
            try
            {
                using (OperationCorrelation.Enter(id))
                    await send(id);
            }
            catch (StackerException ex) when (ex.Problem?.Certainty == EffectCertainty.Confirmed) { executionProblem = ex.Message; }
            phase = AttemptPhase.Acknowledged;
            Confirm(pending.Kind);
            await owner.FlushAsync();
            if (executionProblem is not null)
                owner.PublicationProblem = $"GitHub confirmed the action. The local execution record reported an error. {executionProblem}";
            owner.Message = owner.PublicationProblem ?? success;
            if (resolution is not null)
                await owner.ReloadAfterPublicationAsync();
        }
        catch (Exception ex) { ReportFailure(ex, phase, marker); }
        finally { owner.IsBusy = false; }
        if (resolution is null && phase == AttemptPhase.Acknowledged)
            _ = owner.ReloadAfterPublicationAsync();
    }

    public async Task UnlockRetryAsync()
    {
        if (owner.IsBusy || !owner.HasPendingPublication)
            return;
        owner.IsBusy = true;
        try
        {
            Reject();
            await owner.FlushAsync();
            owner.PublicationProblem = null;
            owner.Message = "Retry unlocked after your manual GitHub check.";
        }
        catch (Exception ex) { owner.PublicationProblem = owner.Message = ex.Message; }
        finally { owner.IsBusy = false; }
    }

    public void Confirm(PendingKind kind)
    {
        var draft = owner._draft;
        var composerKey = draft.PendingComposerKey;
        var filePath = draft.PendingFilePath;
        var wasLoading = owner._loading;
        owner._loading = true;
        try
        {
            ReviewSessionService.Confirm(draft, kind);
            switch (kind)
            {
                case PendingKind.Review:
                    owner.Summary = draft.Summary;
                    foreach (var comment in owner.DraftComments.Where(comment => !draft.Comments.Contains(comment)).ToArray())
                        owner.DraftComments.Remove(comment);
                    break;
                case PendingKind.FileComment when filePath is not null && owner.SelectedReviewFile == filePath:
                    owner.FileComposer = draft.FileComposers.GetValueOrDefault(filePath, "");
                    break;
                case PendingKind.Comment or PendingKind.Inline or PendingKind.Reply when composerKey is not null && composerKey == owner._composerKey.Encoded:
                    owner.Composer = draft.ContextComposers.GetValueOrDefault(composerKey, composerKey == "pr" ? draft.Composer : "");
                    break;
            }
        }
        finally { owner._loading = wasLoading; }
        owner.QueueSave();
    }

    private void Reject()
    {
        ReviewSessionService.Reject(owner._draft);
        owner.QueueSave();
    }

    private void ReportFailure(Exception error, AttemptPhase phase, string marker)
    {
        var detail = error.Message;
        switch (phase)
        {
            case AttemptPhase.Acknowledged:
                detail = "GitHub confirmed the action. Could not save the local confirmation. " + detail;
                break;
            case AttemptPhase.Dispatching when error is not StackerException { Problem.Certainty: EffectCertainty.NotStarted or EffectCertainty.Rejected }:
                ReviewSessionService.Transition(owner._draft, PublicationPhase.Uncertain);
                owner.QueueSave();
                detail += " GitHub may have received this action. Check publication status before retrying.";
                break;
            default:
                if (owner._draft.PendingOperation == marker)
                    Reject();
                break;
        }
        owner.PublicationProblem = detail;
        owner.Message = detail;
    }
}
