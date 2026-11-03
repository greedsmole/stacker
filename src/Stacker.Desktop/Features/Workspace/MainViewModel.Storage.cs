using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private string _storageStatus="";
    public Task RefreshStorageStatusAsync()=>ApplyCacheBudgetAsync();
    private async Task ApplyCacheBudgetAsync()
    {
        if(_runtime.Objects is null)return;
        Settings.CacheBudgetMiB=Math.Clamp(Settings.CacheBudgetMiB,64,65536);
        _runtime.Objects.SetBudget((long)Settings.CacheBudgetMiB*1024*1024).OrThrow();
        var result=await _runtime.Objects.TrimAsync();
        if(result.IsFailed){StorageStatus=result.Problem!.Message;return;}
        var status=result.Value!;
        StorageStatus=$"Downloads: {status.DisposableBytes/1048576d:F0} MiB / {Settings.CacheBudgetMiB} MiB. History: {status.HistoryBytes/1048576d:F0} MiB.";
        if(status.HistoryWarning)StorageStatus+=" History exceeds 5 GiB. Export a backup before explicitly forgetting old history.";
    }
    public RepositoryId? CurrentRepositoryId=>_runtime._repository?.Identity?.Repository;
}
