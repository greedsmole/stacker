// Generated presentation boundary; application state lives in WorkspaceRuntime/WorkspaceSession.
namespace Stacker.Desktop.ViewModels;

internal interface INavigationPresentation
{
    global::Stacker.Desktop.ViewModels.ReviewViewModel? ActiveReview
    {
        get; set;
    }
    global::System.String BaseLabel
    {
        get;
    }
    global::System.Boolean CanEdit
    {
        get; set;
    }
    global::System.Boolean CanRestackActiveStack
    {
        get;
    }
    global::System.Boolean CanReview
    {
        get;
    }
    global::System.Threading.Tasks.Task CaptureHistorySectionAsync(global::Stacker.Desktop.ViewModels.DiffSectionViewModel @section, global::System.Threading.CancellationToken @ct);
    global::System.Threading.Tasks.Task ClearReviewSessionsAsync();
    void CommandStarting();
    global::System.String ComparisonDetail
    {
        get;
    }
    global::System.String ComparisonTitle
    {
        get; set;
    }
    void DisposeReviewSessions();
    global::System.String EmptyMessage
    {
        get; set;
    }
    global::System.Threading.Tasks.Task<global::Stacker.Desktop.ViewModels.ReviewViewModel?> EnsureReviewAsync(global::Stacker.Desktop.ViewModels.DiffSectionViewModel @section, global::System.Threading.CancellationToken @ct = default);
    global::System.String Error
    {
        get; set;
    }
    global::Stacker.Core.PullRequest? FindLocalPr(global::System.String @branch);
    global::System.String GitHubStatus
    {
        get; set;
    }
    global::System.String GroupKey(global::Stacker.Desktop.ViewModels.StackGroupViewModel @group);
    global::System.Boolean HasPendingChanges
    {
        get; set;
    }
    global::System.Boolean HasPrDiscussion
    {
        get;
    }
    global::System.Boolean HasSelectedLayer
    {
        get;
    }
    global::System.Boolean IsBusy
    {
        get; set;
    }
    global::System.Boolean IsDemo
    {
        get; set;
    }
    global::System.Boolean IsLocalSelected
    {
        get;
    }
    global::System.Boolean IsReviewOpen
    {
        get; set;
    }
    global::System.Boolean IsSyncing
    {
        get; set;
    }
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.LayerViewModel> Layers
    {
        get;
    }
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.StackGroupViewModel> LocalGroups
    {
        get;
    }
    void LogChanged(global::Stacker.Core.GitCommandEntry? @entry);
    global::Stacker.Core.DiffMode Mode
    {
        get; set;
    }
    void Notify(global::System.String @name);
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.StackGroupViewModel> OtherGroups
    {
        get;
    }
    global::System.Threading.Tasks.Task<global::Stacker.Core.RepositorySnapshot> PrepareRemoteAsync(global::Stacker.Desktop.ViewModels.StackGroupViewModel @group, global::Stacker.Core.DiffMode @mode, global::System.Int32[] @positions, global::System.Threading.CancellationToken @ct);
    global::System.Collections.ObjectModel.ObservableCollection<global::System.String> RecentRepositories
    {
        get;
    }
    global::System.Threading.Tasks.Task RefreshGitHubAsync();
    global::System.Threading.Tasks.Task RefreshSyncAvailabilityAsync(global::System.Threading.CancellationToken @ct = default);
    global::System.Threading.Tasks.Task<(bool Ok, string Message)> RefreshWorkingTreeAsync(global::System.Threading.CancellationToken @ct = default);
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.StackGroupViewModel> RemoteGroups
    {
        get;
    }
    void ReportCommand(global::Stacker.Core.Framework.CommandOutcome @outcome);
    global::System.String RepositoryPath
    {
        get; set;
    }
    global::System.String RepositoryTitle
    {
        get; set;
    }
    void ResetSearch();
    void RestoreSections();
    global::System.Threading.Tasks.Task SaveWorkspaceAsync();
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.DiffSectionViewModel> Sections
    {
        get;
    }
    global::Stacker.Core.StackDefinition? SelectedStack
    {
        get; set;
    }
    global::Stacker.Core.AppSettings Settings
    {
        get; set;
    }
    void SetWindowActive(global::System.Boolean @active);
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Core.StackDefinition> Stacks
    {
        get;
    }
    global::System.String Status
    {
        get; set;
    }
}
