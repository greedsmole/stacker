// Generated presentation boundary; application state lives in WorkspaceRuntime/WorkspaceSession.
namespace Stacker.Desktop.ViewModels;

internal interface ISyncPresentation
{
    global::System.String Error { get; set; }
    global::System.Boolean HasPendingChanges { get; set; }
    global::System.Boolean IsDemo { get; set; }
    global::System.Boolean IsSyncing { get; set; }
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.StackGroupViewModel> LocalGroups { get; }
    global::System.Threading.Tasks.Task RefreshAsync();
    global::System.Threading.Tasks.Task RefreshGitHubAsync();
    global::System.Threading.Tasks.Task RefreshGitHubAfterPublicationAsync();
    global::System.Threading.Tasks.Task<(bool Ok, string Message)> RefreshWorkingTreeAsync(global::System.Threading.CancellationToken @ct = default);
    global::System.Threading.Tasks.Task<global::System.Boolean> SaveStackAsync(global::Stacker.Core.StackDefinition @stack);
    global::System.String Status { get; set; }
    global::System.Threading.Tasks.Task UpdateChangesAsync();
}
