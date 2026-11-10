// Generated presentation boundary; application state lives in WorkspaceRuntime/WorkspaceSession.
namespace Stacker.Desktop.ViewModels;

internal interface IGitHubPresentation
{
    global::System.String BaseLabel { get; }
    global::System.Boolean CanReview { get; }
    global::System.Threading.Tasks.Task CompareAsync();
    void DisposeViews();
    global::System.String EmptyMessage { get; set; }
    global::System.String Error { get; set; }
    global::System.Boolean GitHubBusy { get; set; }
    global::Stacker.Core.RepositoryPreferences GitHubPreferences { get; set; }
    global::System.String GitHubStatus { get; set; }
    global::System.Boolean HasOtherGroups { get; }
    global::System.Boolean HasPendingChanges { get; set; }
    global::System.Boolean HasPrDiscussion { get; }
    global::System.Boolean HasRemoteGroups { get; }
    global::System.Boolean HasSelectedLayer { get; }
    global::System.Boolean IsBusy { get; set; }
    global::System.Boolean IsDemo { get; set; }
    global::System.Boolean IsLocalSelected { get; }
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.LayerViewModel> Layers { get; }
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.StackGroupViewModel> LocalGroups { get; }
    void Notify(global::System.String @name);
    global::System.Threading.Tasks.Task OpenRepositoryAsync(global::System.String @path);
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.StackGroupViewModel> OtherGroups { get; }
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.StackGroupViewModel> RemoteGroups { get; }
    global::System.Threading.Tasks.Task<global::System.Boolean> SaveStackAsync(global::Stacker.Core.StackDefinition @stack);
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.DiffSectionViewModel> Sections { get; }
    global::Stacker.Core.PullRequest? SelectedPrForDiscussion { get; }
    global::Stacker.Core.StackDefinition? SelectedStack { get; set; }
    global::System.Threading.Tasks.Task SelectLayerAsync(global::Stacker.Desktop.ViewModels.LayerViewModel @layer, global::System.Boolean @toggle);
    void SetActiveGroup(global::Stacker.Desktop.ViewModels.StackGroupViewModel @group);
}
