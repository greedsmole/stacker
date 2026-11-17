// Generated presentation boundary; application state lives in WorkspaceRuntime/WorkspaceSession.
namespace Stacker.Desktop.ViewModels;

internal interface ISearchPresentation
{
    global::System.Boolean IsGitLogOpen
    {
        get; set;
    }
    global::System.Boolean IsSearchBusy
    {
        get; set;
    }
    global::System.Boolean IsSearchOpen
    {
        get; set;
    }
    global::System.String SearchQuery
    {
        get; set;
    }
    global::System.Boolean SearchRegex
    {
        get; set;
    }
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.ChangeSearchHit> SearchResults
    {
        get;
    }
    global::System.String SearchScope
    {
        get; set;
    }
    global::System.String SearchStatus
    {
        get; set;
    }
    global::System.Collections.ObjectModel.ObservableCollection<global::Stacker.Desktop.ViewModels.DiffSectionViewModel> Sections
    {
        get;
    }
}
