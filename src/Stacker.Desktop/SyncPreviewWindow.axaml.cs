// Purpose: Shows what a restack would rewrite, what the layers' pull requests need, and how to finish a conflict in the user's editor.
using Avalonia.Controls;
using Avalonia.Interactivity;
using Stacker.Core;
namespace Stacker.Desktop;
public partial class SyncPreviewWindow : Window
{
    private readonly ISyncActions? _sync;
    private RestackPlan? _plan;
    private PullRequestPublicationPlan? _publication;
    private bool _restacked;
    public SyncPreviewWindow() { InitializeComponent(); }
    public SyncPreviewWindow(RestackPlan plan, ISyncActions sync, bool undoAvailable) : this()
    {
        _plan = plan; _sync = sync;
        HeadingText.Text = "Sync · " + plan.StackName;
        BlockerList.ItemsSource = plan.Blockers;
        BlockerList.IsVisible = plan.Blockers.Count > 0;
        StepList.ItemsSource = plan.Steps;
        PreserveMergesToggle.IsChecked = plan.PreserveMerges;
        RestackButton.IsEnabled = plan.CanExecute;
        UndoButton.IsVisible = undoAvailable;
        var branches = plan.Steps.Where(step => step.IsLocalBranch).Select(step => step.BranchName).ToArray();
        BranchInput.ItemsSource = branches;
        if (branches.Length > 0) BranchInput.SelectedIndex = 0;
        PushButton.IsVisible = plan.HasOrigin && plan.StackPushSteps.Count > 0;
        Render(plan, sync.WorkingTree);
    }
    /// <summary>Re-renders everything the working tree and the plan decide.</summary>
    private void Render(RestackPlan plan, WorkingTreeState? state)
    {
        var attention = state is { NeedsAttention: true };
        ConflictPanel.IsVisible = attention;
        ConflictTitle.Text = attention ? state!.Summary : "";
        ConflictList.ItemsSource = state?.ConflictedFiles ?? [];
        ResolveButton.IsEnabled = state is { HasConflicts: true };
        StageButton.IsEnabled = state is { HasConflicts: true };
        ContinueButton.IsVisible = state is { CanContinue: true };
        SkipButton.IsVisible = state is { CanContinue: true };
        AbortButton.IsVisible = state is { CanAbort: true };
        WorkingTreeLabel.Text = state is null ? "" : "Working tree: " + state.Summary;
        StashButton.IsVisible = !_restacked && !plan.CanExecute && state is { HasTrackedChanges: true };
        RestoreButton.IsVisible = _sync?.CanRestoreStash == true;
        SubheadingText.Text = _restacked
            ? "The branches were rewritten locally. Publish them, or close this window to review the new snapshots."
            : plan.CanExecute
                ? $"{plan.RebaseCount} layer(s) will be replayed onto their parent. Review the list below, then start the local restack."
                : plan.Blockers.Count > 0 ? "Nothing runs until every item below is resolved." : "Every layer is already based on its parent. There is nothing to rewrite.";
    }
    private void PreserveClick(object? sender, RoutedEventArgs e) => _ = ReplanAsync();
    /// <summary>Re-plans with or without merge preservation so the preview never lies about what will run.</summary>
    private async Task ReplanAsync()
    {
        if (_sync is null || _plan is null) return;
        var preserve = PreserveMergesToggle.IsChecked == true;
        if (preserve == _plan.PreserveMerges) return;
        IsEnabled = false;
        try
        {
            var plan = await _sync.BuildRestackPlanAsync(preserve);
            if (plan is null || plan.StackId != _plan.StackId) { MessageLabel.Text = "The stack could not be planned again."; return; }
            _plan = plan;
            BlockerList.ItemsSource = plan.Blockers;
            BlockerList.IsVisible = plan.Blockers.Count > 0;
            StepList.ItemsSource = plan.Steps;
            RestackButton.IsEnabled = plan.CanExecute;
            PushButton.IsVisible = plan.HasOrigin && plan.StackPushSteps.Count > 0;
            MessageLabel.Text = preserve ? "Merge commits inside a layer will be preserved." : "Merge commits inside a layer block the restack again.";
            Render(plan, _sync.WorkingTree);
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
        finally { IsEnabled = true; }
    }
    private async void RestackClick(object? sender, RoutedEventArgs e)
    {
        if (_plan is null || _sync is null) return;
        RestackButton.IsEnabled = false; RestackButton.Content = "Restacking…"; MessageLabel.Text = "";
        try
        {
            var outcome = await _sync.RestackAsync(_plan, new Progress<string>(text => MessageLabel.Text = text));
            MessageLabel.Text = outcome is null ? "The restack did not start." : outcome.Message;
            if (outcome is { Applied: true } or { Conflicted: true })
            {
                _restacked = outcome.Applied;
                RestackButton.Content = outcome.Applied ? "Restacked" : "Restack (local)";
                RestackButton.IsEnabled = !outcome.Applied;
                UndoButton.IsVisible = outcome.Applied && outcome.Steps.Count > 0;
                UndoButton.IsEnabled = true;
            }
            else { RestackButton.Content = "Restack (local)"; RestackButton.IsEnabled = true; }
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; RestackButton.Content = "Restack (local)"; RestackButton.IsEnabled = true; }
        finally { if (_plan is not null) Render(_plan, _sync.WorkingTree); }
    }
    private async void PushClick(object? sender, RoutedEventArgs e)
    {
        if (_plan is null || _sync is null) return;
        PushButton.IsEnabled = false; MessageLabel.Text = "Publishing the branches…";
        try
        {
            var (ok, message) = await _sync.PushAsync(_plan, wholeStack: true);
            MessageLabel.Text = message;
            if (ok) PushButton.IsVisible = false; else PushButton.IsEnabled = true;
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; PushButton.IsEnabled = true; }
    }
    private async void ResolveClick(object? sender, RoutedEventArgs e) => await RunAsync(() => _sync!.OpenConflictsInEditorAsync());
    private async void StageClick(object? sender, RoutedEventArgs e) => await RunAsync(() => _sync!.StageConflictsAsync());
    private async void ContinueClick(object? sender, RoutedEventArgs e) => await RunAsync(() => _sync!.ContinueOperationAsync());
    private async void SkipClick(object? sender, RoutedEventArgs e) => await RunAsync(() => _sync!.SkipCommitAsync());
    private async void AbortClick(object? sender, RoutedEventArgs e) => await RunAsync(() => _sync!.AbortOperationAsync());
    private async void StashClick(object? sender, RoutedEventArgs e)
    {
        if (_plan is null || _sync is null) return;
        await RunAsync(async () =>
        {
            var result = await _sync.StashAndRestackAsync(_plan);
            if (result.Ok) _restacked = true;
            return result;
        });
    }
    private async void RestoreClick(object? sender, RoutedEventArgs e) => await RunAsync(() => _sync!.RestoreStashAsync());
    private async void SwitchClick(object? sender, RoutedEventArgs e)
    {
        if (_sync is null || BranchInput.SelectedItem is not string branch) { MessageLabel.Text = "Choose a branch to switch to."; return; }
        await RunAsync(() => _sync.SwitchToBranchAsync("refs/heads/" + branch));
    }
    private async Task RunAsync(Func<Task<(bool Ok, string Message)>> action)
    {
        if (_sync is null) return;
        IsEnabled = false;
        try { var (_, message) = await action(); MessageLabel.Text = message; }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
        finally { IsEnabled = true; if (_plan is not null) Render(_plan, _sync.WorkingTree); }
    }
    private async void CheckPrsClick(object? sender, RoutedEventArgs e)
    {
        if (_plan is null || _sync is null) return;
        CheckPrsButton.IsEnabled = false; MessageLabel.Text = "Reading the open pull requests…";
        try
        {
            var (publication, message) = await _sync.CheckPullRequestsAsync(_plan.StackId);
            MessageLabel.Text = message;
            if (publication is null) { PublishButton.IsVisible = false; PullRequestList.IsVisible = false; return; }
            _publication = publication;
            PullRequestList.ItemsSource = publication.Steps;
            PullRequestList.IsVisible = true;
            PublishButton.IsVisible = true;
            PublishButton.IsEnabled = publication.CanPublish;
            PublishButton.Content = publication.CanPublish ? $"Publish {publication.PendingCount} pull request(s)" : "Nothing to publish";
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; }
        finally { CheckPrsButton.IsEnabled = true; }
    }
    private async void PublishClick(object? sender, RoutedEventArgs e)
    {
        if (_sync is null || _publication is null) return;
        PublishButton.IsEnabled = false; CheckPrsButton.IsEnabled = false; MessageLabel.Text = "Publishing pull requests…";
        try
        {
            var (ok, message) = await _sync.PublishPullRequestsAsync(_publication);
            MessageLabel.Text = message;
            if (ok) { PublishButton.IsVisible = false; PullRequestList.IsVisible = false; _publication = null; }
            else PublishButton.IsEnabled = true;
        }
        catch (Exception ex) { MessageLabel.Text = ex.Message; PublishButton.IsEnabled = true; }
        finally { CheckPrsButton.IsEnabled = true; }
    }
    private async void UndoClick(object? sender, RoutedEventArgs e) => await RunAsync(() => _sync!.UndoAsync());
    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
