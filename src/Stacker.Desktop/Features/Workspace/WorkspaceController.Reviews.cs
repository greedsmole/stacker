using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;
namespace Stacker.Desktop.ViewModels;

internal sealed partial class WorkspaceController
{
    public async Task<ReviewViewModel?> EnsureReviewAsync(DiffSectionViewModel section, CancellationToken ct = default)
    {
        if (section.AssociatedPr is not { } pr || section.GitHubContext is not { } context || runtime._github is null || runtime._writer is null)
            return null;
        await runtime._reviewGate.WaitAsync(ct);
        try
        {
            var key = context.Identity + "/" + pr.Number;
            if (!runtime._reviews.TryGetValue(key, out var session))
            {
                if (runtime._reviews.Count >= 12)
                {
                    var staleKey = runtime._reviewOrder.FirstOrDefault(candidate => !runtime._reviews[candidate].IsBusy
                        && !ReferenceEquals(runtime._reviews[candidate], owner.ActiveReview));
                    if (staleKey is null)
                        throw new StackerException("Review sessions are busy. Finish the current operation before opening another.");
                    var stale = runtime._reviews[staleKey];
                    await stale.FlushAsync();
                    stale.PropertyChanged -= ReviewChanged;
                    stale.Dispose();
                    runtime._reviews.Remove(staleKey);
                    runtime._reviewOrder.Remove(staleKey);
                }
                session = new(runtime._github, runtime._writer, runtime._applicationStore, runtime._git, context, pr, section, owner.IsDemo)
                {
                    HistoryObjects = runtime.HistoryObjects,
                    HistoryRepository = runtime._repository?.Identity
                };
                try
                {
                    await session.InitializeAsync(ct: ct);
                    ct.ThrowIfCancellationRequested();
                    if (runtime._disposed)
                        throw new OperationCanceledException("The workspace was closed.");
                }
                catch { session.Dispose(); throw; }
                runtime._reviews[key] = session;
                session.PropertyChanged += ReviewChanged;
            }
            runtime._reviewOrder.Remove(key);
            runtime._reviewOrder.AddLast(key);
            session.Attach(section);
            return session;
        }
        finally { runtime._reviewGate.Release(); }
    }

    private void ReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!runtime._disposed && e.PropertyName == nameof(ReviewViewModel.ReviewLabel))
            owner.Notify(nameof(owner.ReviewLabel));
    }

    public async Task ClearReviewsAsync()
    {
        await runtime._reviewGate.WaitAsync();
        try
        {
            foreach (var session in runtime._reviews.Values)
                await session.FlushAsync();
            DisposeReviews();
        }
        finally { runtime._reviewGate.Release(); }
    }
    public void DisposeReviews()
    {
        foreach (var session in runtime._reviews.Values)
        {
            session.PropertyChanged -= ReviewChanged;
            session.Dispose();
        }
        runtime._reviews.Clear();
        runtime._reviewOrder.Clear();
    }

    public async Task ShowReviewAsync(DiffSectionViewModel? section = null, int tab = 3)
    {
        section ??= owner.Sections.FirstOrDefault(s => s.IsPrSnapshot) ?? owner.CreateDiscussionContext();
        if (section is null)
            return;
        try
        {
            owner.ActiveReview = await EnsureReviewAsync(section);
        }
        catch (Exception ex) { owner.Error = ex.Message; return; }
        if (owner.ActiveReview is not null)
        {
            owner.ActiveReview.PanelTab = tab;
            if (tab == 0)
                owner.ActiveReview.BeginPrComment();
            owner.IsReviewOpen = true;
        }
    }

    public void CloseReview() => owner.IsReviewOpen = false;

    public async Task FlushReviewsAsync()
    {
        foreach (var review in runtime._reviews.Values)
            await review.FlushAsync();
    }
}
