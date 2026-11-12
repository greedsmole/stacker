using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Desktop.ViewModels;

public partial class ReviewViewModel
{
    internal CancellationTokenSource? _discussionLoad;

    [RelayCommand]
    public Task ReloadAsync(CancellationToken ct = default) => ReadDiscussionAsync(ct);

    internal Task ReloadAfterPublicationAsync() => ReadDiscussionAsync(default, afterPublication: true);

    /// <summary>Reloads remote comments while retaining unsent local draft text.</summary>
    public Task RefreshDiscussionAsync(CancellationToken ct = default) =>
        IsBusy ? Task.CompletedTask : ReadDiscussionAsync(ct, throwOnFailure: true);

    private async Task ReadDiscussionAsync(CancellationToken ct, bool afterPublication = false, bool throwOnFailure = false)
    {
        _discussionLoad?.Cancel();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(ct); _discussionLoad = load;
        var snapshot = PullRequest;
        try
        {
            var discussion = IsDemo ? DemoReviewData.Discussion(PullRequest) : (await _reader.DiscussionAsync(Context, snapshot, load.Token)).OrThrow();
            load.Token.ThrowIfCancellationRequested();
            if (snapshot != PullRequest) return;
            Sync(Comments, discussion.Comments, c => c.Id); Sync(Reviews, discussion.Reviews, r => r.Id);
            var selectedId = SelectedThread?.Id;
            Sync(Threads, discussion.Threads, t => t.Id);
            SelectedThread = Threads.FirstOrDefault(t => t.Id == selectedId);
            foreach (var view in _sections) view.SetThreads(Threads);
            Sync(CodeThreads, Threads.Where(t => !t.IsFileLevel).ToArray(), t => t.Id);
            DiscussionChanged?.Invoke();
            foreach (var path in discussion.Threads.Select(t => t.Path).OfType<string>().Concat(_draft.FileComposers.Keys).Distinct())
                if (!ReviewFiles.Contains(path)) ReviewFiles.Add(path);
            UpdateFileThreads();
            if (_draft.PendingOperation is { } pendingText)
            {
                if (!PendingOperation.TryParse(pendingText, out var pending))
                {
                    Message = "The saved publication marker is unreadable. Verify the action on GitHub before unlocking a retry. Your draft is saved.";
                    QueueSave();
                }
                else
                {
                    var marker = "<!-- stacker:" + pending.Marker + " -->";
                    var found = pending.Kind == PendingKind.Resolve
                        ? discussion.Threads.Any(t => t.Id == pending.ThreadId && t.IsResolved == pending.Resolved)
                        : discussion.Comments.Any(c => c.Author == Context.User && c.Body.Contains(marker, StringComparison.Ordinal))
                          || discussion.Reviews.Any(r => r.Author == Context.User && r.Body.Contains(marker, StringComparison.Ordinal))
                          || discussion.Threads.SelectMany(t => t.Comments).Any(c => c.Author == Context.User && c.Body.Contains(marker, StringComparison.Ordinal));
                    if (found)
                    {
                        ReviewSessionService.Transition(_draft, PublicationPhase.Confirmed);
                        if (pending.Kind == PendingKind.Review) ClearSubmittedDraft();
                        else if (pending.Kind == PendingKind.FileComment && _draft.PendingFilePath is { } path) ClearPublishedFile(path);
                        else if (pending.Kind is PendingKind.Comment or PendingKind.Inline or PendingKind.Reply) ClearPublishedComposer();
                        _draft.PendingOperation = null; _draft.PendingFilePath = null; PublicationProblem = null;
                        Message = "Previous publication confirmed on GitHub."; QueueSave();
                    }
                    else Message = "Publication is still uncertain. Verify the action on GitHub before unlocking a retry.";
                }
                if (!throwOnFailure && HasPendingPublication) PublicationProblem = Message;
            }
            NotifyPublicationState();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) when (load.IsCancellationRequested || snapshot != PullRequest) { }
        catch (Exception ex)
        {
            Message = afterPublication ? "Published on GitHub. Could not refresh the discussion. " + ex.Message : ex.Message;
            if (afterPublication) PublicationProblem ??= Message;
            else if (!throwOnFailure && HasPendingPublication) PublicationProblem = "Could not check publication status. " + ex.Message;
            if (throwOnFailure) throw;
        }
        finally { if (ReferenceEquals(_discussionLoad, load)) _discussionLoad = null; }
    }
    internal static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> incoming, Func<T, string> key)
    {
        for (var i = target.Count - 1; i >= 0; i--) if (!incoming.Any(v => key(v) == key(target[i]))) target.RemoveAt(i);
        for (var i = 0; i < incoming.Count; i++)
        {
            var old = target.FirstOrDefault(v => key(v) == key(incoming[i]));
            if (old is null) target.Insert(i, incoming[i]);
            else { var index = target.IndexOf(old); if (index != i) target.Move(index, i); if (JsonSerializer.Serialize(old) != JsonSerializer.Serialize(incoming[i])) target[i] = incoming[i]; }
        }
    }
}
