namespace Stacker.Core;

public sealed record WorkspaceSelection(string? ChangeId,string? ComparisonId,string? Path);
public sealed record WorkspaceSnapshot(long Revision,RepositorySnapshot? Repository,GitHubSnapshot? Remote,GraphSnapshot? Graph);
public sealed record QueryTicket(long Epoch,long SelectionRevision,long SnapshotRevision);
public interface IWorkspaceSession
{
    WorkspaceSnapshot Displayed { get; }
    WorkspaceSnapshot? Pending { get; }
    WorkspaceSelection Selection { get; }
    QueryTicket Ticket { get; }
    void Open(RepositorySnapshot repository);
    void Offer(GitHubSnapshot snapshot,GraphSnapshot? graph);
    void ApplyPending();
    void SetRemote(GitHubSnapshot? snapshot, GraphSnapshot? graph);
    void DiscardPending();
    void Select(WorkspaceSelection selection);
    bool IsCurrent(QueryTicket ticket);
    void Suspend();
}
/// <summary>The application state is independent of controls and dispatchers. Only explicit Apply replaces displayed data.</summary>
public sealed class WorkspaceSession : IWorkspaceSession
{
    private long _epoch;
    private long _selectionRevision;
    public WorkspaceSnapshot Displayed { get; private set; } = new(0,null,null,null);
    public WorkspaceSnapshot? Pending { get; private set; }
    public WorkspaceSelection Selection { get; private set; } = new(null,null,null);
    public QueryTicket Ticket => new(_epoch,_selectionRevision,Displayed.Revision);
    public void Open(RepositorySnapshot repository)
    {
        if(Displayed.Repository?.Root!=repository.Root){_epoch++;Selection=new(null,null,null);_selectionRevision++;Pending=null;Displayed=new(Displayed.Revision+1,repository,null,null);}
        else Displayed=Displayed with{Revision=Displayed.Revision+1,Repository=repository};
    }
    public void Offer(GitHubSnapshot snapshot,GraphSnapshot? graph)
    {
        if(Displayed.Remote?.Context.Identity is { } owner && owner!=snapshot.Context.Identity){_epoch++;_selectionRevision++;}
        Pending=Displayed with{Revision=Displayed.Revision+1,Remote=snapshot,Graph=graph};
    }
    public void SetRemote(GitHubSnapshot? snapshot,GraphSnapshot? graph)
    {
        if(Displayed.Remote?.Context.Identity!=snapshot?.Context.Identity){_epoch++;_selectionRevision++;}
        Displayed=Displayed with{Revision=Displayed.Revision+1,Remote=snapshot,Graph=graph};
    }
    public void DiscardPending()=>Pending=null;
    public void ApplyPending(){if(Pending is null)return;Displayed=Pending;Pending=null;_selectionRevision++;}
    public void Select(WorkspaceSelection selection){if(selection==Selection)return;Selection=selection;_selectionRevision++;}
    public bool IsCurrent(QueryTicket ticket)=>ticket==Ticket;
    public void Suspend(){_epoch++;_selectionRevision++;}
}
