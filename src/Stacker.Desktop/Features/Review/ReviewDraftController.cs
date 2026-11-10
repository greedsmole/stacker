using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

/// <summary>Loads, persists and flushes one review editor's draft state.</summary>
/// <remarks>Draft writes are serialized through <see cref="CoalescingWriter{T}"/> so rapid changes cannot race or leave an
/// older snapshot after a newer one.</remarks>
internal sealed class ReviewDraftController(ReviewViewModel owner)
{
    /// <summary>Pending serialized draft writer, created after the first change.</summary>
    private CoalescingWriter<ReviewDraft>? _buffer;
    /// <summary>Loads the draft, restores buffers and refreshes reviewable files.</summary>
    /// <param name="selection">Optional line selection restored after loading.</param>
    /// <param name="ct">Cancellation requested by the owning session.</param>
    public async Task InitializeAsync(IEnumerable<RenderedDiffLine>? selection = null, CancellationToken ct = default)
    {
        owner._loading = true;
        try
        {
            owner._draft = (await owner.Session.LoadAsync(owner.Key, ct)).OrThrow() ?? new() { ContextIdentity = owner.Context.Identity, PullRequest = owner.PullRequest.Number, BaseSha = owner.PullRequest.BaseSha, HeadSha = owner.PullRequest.HeadSha, EditorVersion = 1 };
            owner._draftLoaded = true;
            if (owner._draft.EditorVersion == 0) { owner._draft.RecoveredText = owner._draft.Composer; owner._draft.Composer = ""; owner._draft.EditorVersion = 1; }
            owner.RecoverDetachedEditors();
            owner.Summary = owner._draft.Summary; owner.Decision = owner._draft.Decision; owner.Composer = owner._draft.ContextComposers.GetValueOrDefault("pr", owner._draft.Composer);
            owner.Notify(nameof(owner.RecoveredText)); owner.Notify(nameof(owner.HasRecoveredText));
            owner._draft.FileComposers ??= [];
            IReadOnlyList<string> files = owner._section.IsPrSnapshot ? owner._section.Result.Files.Select(f => f.NewPath).ToArray()
                : owner.IsDemo ? [] : (await owner._reader.ChangedFilePathsAsync(owner.Context, owner.PullRequest, ct)).OrThrow();
            foreach (var file in files) { owner.ReviewFiles.Add(file); owner._reviewableFiles.Add(file); }
            owner.SelectedReviewFile = owner._section.SelectedFile?.NewPath ?? owner.ReviewFiles.FirstOrDefault();
            owner.DraftComments.Clear(); foreach (var comment in owner._draft.Comments) owner.DraftComments.Add(comment);
            if (selection is not null) owner.SetSelection(selection);
            await owner.ReloadAsync(ct);
            if (owner._draft.HeadSha != owner.PullRequest.HeadSha || owner._draft.BaseSha != owner.PullRequest.BaseSha) owner.Message = "Draft belongs to an older PR version. Keep its text; recheck/remove line anchors before using the current version.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { owner.Message = ex.Message; }
        finally { owner._loading = false; owner.NotifyPublicationState(); QueueSave(); }
    }

    /// <summary>Queues an immutable draft copy unless loading is still in progress.</summary>
    public void QueueSave()
    {
        if (owner._loading || !owner._draftLoaded) return;
        var copy = JsonSerializer.Deserialize<ReviewDraft>(JsonSerializer.Serialize(owner._draft))!;
        _buffer ??= new(copy => SaveOrderedAsync(Task.CompletedTask, copy));
        owner._saveQueue = _buffer.Queue(copy);
    }

    /// <summary>Waits for the previous write and persists the next snapshot.</summary>
    /// <param name="previous">Preceding save task.</param><param name="copy">Detached draft snapshot.</param>
    public async Task SaveOrderedAsync(Task previous, ReviewDraft copy)
    {
        await previous;
        try { (await owner.Session.SaveAsync(owner.Key, copy)).OrThrow(); owner._saveError = null; }
        catch (Exception ex)
        {
            var error = "Cannot save draft: " + ex.Message; owner._saveError = error;
            Dispatcher.UIThread.Post(() => { if (owner._saveError == error) owner.Message = error; });
        }
    }

    /// <summary>Waits for queued writes and throws when the last write failed.</summary>
    public async Task FlushAsync() { if (_buffer is not null) await _buffer.FlushAsync(); await owner._saveQueue; if (owner._saveError is not null) throw new StackerException(owner._saveError); }

}
