using Stacker.Core;
using CommunityToolkit.Mvvm.ComponentModel;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IWorkspaceBackupService? _backupService;
    [ObservableProperty] private string? _restoredWorkspace;
    public bool CanOpenRestoredWorkspace => RestoredWorkspace is not null;
    partial void OnRestoredWorkspaceChanged(string? value)=>OnPropertyChanged(nameof(CanOpenRestoredWorkspace));
    public string RecoveryText => string.Join("\n\n",_runtime._reviews.Values.Select(r=>r.ExportRecoveryText()));
    public async Task ExportWorkspaceAsync(string path)
    {
        if(_runtime._commands?.IsBusy==true){Error="Finish the running operation before exporting.";return;}
        await SaveWorkspaceAsync();await FlushReviewsAsync();
        if(_runtime._workspacePersistence.LastProblem is { } problem)throw new StackerException(problem);
        if(_backupService is null){Error="Workspace backup is unavailable.";return;}
        (await _backupService.ExportAsync(path,_runtime._repository?.Identity)).OrThrow();Status="Workspace backup saved.";
    }
    public async Task RestoreWorkspaceAsync(string path,string folder)
    {
        if(_backupService is null){Error="Workspace restore is unavailable.";return;}
        RestoredWorkspace=(await _backupService.RestoreAsync(path,folder)).OrThrow();Status="Backup restored to a separate profile. Open restored workspace to continue.";
    }
    public async Task SaveRecoveryCopyAsync(string path)
    {
        if(_backupService is null)throw new StackerException("Recovery export is unavailable.");
        (await _backupService.SaveRecoveryTextAsync(path,RecoveryText)).OrThrow();
    }
    public void OpenRestoredWorkspace(){if(RestoredWorkspace is not null)_backupService?.OpenRestoredWorkspace(RestoredWorkspace).OrThrow();}
    public async Task<IReadOnlyList<string>> PrepareCloseAsync()
    {
        var problems=new List<string>();
        if(_runtime._commands is not null&&!await _runtime._commands.PauseAsync(TimeSpan.FromSeconds(5)))problems.Add("An operation is still stopping; its journal will be checked on restart.");
        SetWindowActive(false);_runtime._refresh?.Cancel();_runtime._comparison?.Cancel();_runtime._githubLoad?.Cancel();_runtime._searchLoad?.Cancel();_runtime.Session.Suspend();
        try{await SaveWorkspaceAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(Exception ex){problems.Add(ex.Message);}
        if(_runtime._workspacePersistence.LastProblem is { } workspaceError)problems.Add("Workspace: "+workspaceError.Message);
        foreach(var review in _runtime._reviews.Values.ToArray())try{await review.RetrySaveAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(Exception ex){problems.Add("Draft: "+ex.Message);}
        try{await SaveSettingsAsync();}catch(Exception ex){problems.Add("Settings: "+ex.Message);}
        return problems;
    }
    public void CancelClose(){_runtime._commands?.Resume();SetWindowActive(true);}
    public async Task CommitCloseAsync(){if(_runtime._commands is not null)await _runtime._commands.StopAsync(TimeSpan.FromSeconds(5));ActiveHistory?.Dispose();ActiveHistory=null;Dispose();}
}
