using Stacker.Core.Framework;
namespace Stacker.Core;

public enum ContentAvailability { Available,NeedsDownload,Removed,Unavailable }
public sealed class ObjectSnapshotLease(string root,Action release) : IDisposable
{
    private Action? _release=release;
    public string Root { get; }=root;
    public void Dispose()=>Interlocked.Exchange(ref _release,null)?.Invoke();
}
public sealed record ObjectStoreStatus(long DisposableBytes,long HistoryBytes,long DisposableBudget,int ActiveLeases)
{public bool HistoryWarning=>HistoryBytes>=5L*1024*1024*1024;}
public interface IObjectStore
{
    Result<Unit> SetBudget(long bytes);
    Task<Result<ObjectSnapshotLease>> AcquireAsync(string root,bool create=false,CancellationToken ct=default);
    Task<Result<bool>> RemoveAsync(string root,CancellationToken ct=default);
    Task<Result<ObjectStoreStatus>> TrimAsync(CancellationToken ct=default);
}
public sealed record DiagnosticEvent(string OperationId,string Stage,string? ProblemCode,double DurationMs,long Bytes=0);
public interface IDiagnosticSink
{
    Result<Unit> Record(DiagnosticEvent entry);
    Task<Result<string>> ExportAsync(string destination,CancellationToken ct=default);
}
public static class OperationCorrelation
{
    private static readonly AsyncLocal<string?> Active=new();
    public static string? Current=>Active.Value;
    public static IDisposable Enter(string id){var previous=Active.Value;Active.Value=id;return new Scope(()=>Active.Value=previous);}
    private sealed class Scope(Action end):IDisposable{public void Dispose()=>end();}
}
