// Generated presentation boundary; application state lives in WorkspaceRuntime/WorkspaceSession.
namespace Stacker.Desktop.ViewModels;
internal interface IWorkspaceActionsPresentation
{
    global::Stacker.Desktop.ViewModels.ReviewViewModel? ActiveReview { get; set; }
    void BuildDiscoveredGroups();
    global::System.Threading.Tasks.Task CompareAsync();
    global::Stacker.Desktop.ViewModels.DiffSectionViewModel? CreateDiscussionContext();
    void Dispose();
    global::System.String Error { get; set; }
    global::Stacker.Core.RepositoryPreferences GitHubPreferences { get; set; }
    global::System.Boolean HasError { get; }
    global::System.Boolean HasPendingChanges { get; set; }
    global::System.Boolean IsDemo { get; set; }
    global::System.Boolean IsReviewOpen { get; set; }
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.StackGroupViewModel> LocalGroups { get; }
    global::Stacker.Core.DiffMode Mode { get; set; }
    void Notify(global::System.String @name);
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.StackGroupViewModel> OtherGroups { get; }
    global::System.Threading.Tasks.Task RefreshAsync();
    global::System.Threading.Tasks.Task RefreshGitHubCoreAsync(global::System.Threading.CancellationToken @ct,global::System.Boolean @background);
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.StackGroupViewModel> RemoteGroups { get; }
    global::System.String ReviewLabel { get; }
    global::System.Threading.Tasks.Task SaveSettingsAsync();
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.DiffSectionViewModel> Sections { get; }
    void SetActiveGroup(global::Stacker.Desktop.ViewModels.StackGroupViewModel @group);
    global::Stacker.Core.AppSettings Settings { get; set; }
}
