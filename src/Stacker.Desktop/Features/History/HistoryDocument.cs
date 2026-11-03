using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class HistoryWorkspaceViewModel
{
    public ObservableCollection<DiffSectionViewModel> DocumentSections { get; }=[];
    public DiffDocument? Document { get; private set; }
    private readonly Stack<NavigationTarget> _back=[];
    private readonly Stack<NavigationTarget> _forward=[];
    private NavigationTarget? _navigation;
    private bool _restoringNavigation;
    public bool CanGoBack=>!IsBusy&&_back.Count>0;
    public bool CanGoForward=>!IsBusy&&_forward.Count>0;
    partial void OnIsBusyChanged(bool value)=>NotifyNavigation();
    private void NotifyNavigation(){BackCommand.NotifyCanExecuteChanged();ForwardCommand.NotifyCanExecuteChanged();}
    private void RememberNavigation(NavigationTarget target)
    {
        if(_restoringNavigation)return;
        if(_navigation is not null&&_navigation!=target){_back.Push(_navigation);_forward.Clear();}
        _navigation=target;NotifyNavigation();
    }
    private void ClearDocument()
    {foreach(var section in DocumentSections)section.Dispose();DocumentSections.Clear();Document=null;}
    private async Task ShowDocumentAsync(DiffDocument document,string? path=null,string? versionId=null)
    {
        ClearDocument();Document=document;
        if(_current is not null)RememberNavigation(new(_current.ChangeId,versionId,path,document.Mode));
        foreach(var item in document.Sections)
        {
            var result=new DiffResult(item.Title,item.Title,item.BaseSha,item.HeadSha,null,item.Files);
            var view=new DiffSectionViewModel(_git,item.Root,result,readOnly:true);
            if(path is not null)view.SelectedFile=view.Files.FirstOrDefault(f=>f.NewPath==path)??view.Files.FirstOrDefault();
            DocumentSections.Add(view);
            await view.LoadAsync(view.SelectedFile);
        }
        OnPropertyChanged(nameof(Document));
    }
    private async Task SelectDocumentFileAsync(ChangeNode node,FileChange? file)
    {
        await SyncSelectionAsync(node,file);
        if(file is null||Document is null)return;
        if(_navigation is not null)RememberNavigation(_navigation with{Path=file.NewPath});
        foreach(var section in DocumentSections)
        {
            var match=section.Files.FirstOrDefault(f=>f.NewPath==file.NewPath);
            if(match is not null){section.SelectedFile=match;await section.LoadAsync(match);}
        }
    }
    [RelayCommand(CanExecute=nameof(CanGoBack))] private async Task BackAsync()=>await NavigateAsync(_back,_forward);
    [RelayCommand(CanExecute=nameof(CanGoForward))] private async Task ForwardAsync()=>await NavigateAsync(_forward,_back);
    private async Task NavigateAsync(Stack<NavigationTarget> source,Stack<NavigationTarget> destination)
    {
        if(IsBusy||!source.TryPop(out var target))return;
        var previous=_navigation;_restoringNavigation=true;
        try
        {
        SelectedNode=Nodes.FirstOrDefault(n=>n.Id==target.ChangeId);await LoadLayerAsync();
        SelectedFile=Files.FirstOrDefault(f=>f.Change.NewPath==target.Path)??Files.FirstOrDefault();
        PreviousVersion=Versions.FirstOrDefault(v=>v.Id==target.VersionId);
        if(target.Mode==DiffDocumentMode.FileEvolution)await EvolutionAsync();
        else if(target.Mode is DiffDocumentMode.BetweenVersions or DiffDocumentMode.SinceReviewed)await CompareVersionDocumentAsync(target.Mode);
        else await ShowVersionAsync(_current?.Id==target.VersionId?_current:Versions.FirstOrDefault(v=>v.Id==target.VersionId)??_current);
        // Restoring navigation is itself not a new navigation branch.
        if(previous is not null)destination.Push(previous);_navigation=target;
        }
        finally{_restoringNavigation=false;NotifyNavigation();}
    }
}
