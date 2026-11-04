using Stacker.Core.Framework;
namespace Stacker.Core;

public interface IWorkspaceBackupService
{
    Task<Result<string>> ExportAsync(string destination,RepositoryIdentity? repository=null,CancellationToken ct=default);
    Task<Result<string>> RestoreAsync(string archive,string emptyDestination,CancellationToken ct=default);
    Task<Result<Unit>> SaveRecoveryTextAsync(string destination,string text,CancellationToken ct=default);
    Result<Unit> OpenRestoredWorkspace(string root);
}
public enum UnsavedCloseChoice { ContinueEditing,Retry,SaveCopy,Discard }
