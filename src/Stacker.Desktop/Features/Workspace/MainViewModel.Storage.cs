using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private string _storageStatus = "Git objects are stored in the opened repository. Git manages their storage.";
    public async Task RefreshStorageStatusAsync()
    {
        if (_runtime._repository?.Identity is not { } repository || _runtime._prObjects is null)
            return;
        var result = await _runtime._prObjects.InspectAsync(repository);
        StorageStatus = result.IsFailed ? result.Problem!.Message : $"Git objects: {result.Value!.SizeBytes / 1048576d:F1} MiB · {result.Value.CommonDirectory}";
    }
    public RepositoryId? CurrentRepositoryId => _runtime._repository?.Identity?.Repository;
}
