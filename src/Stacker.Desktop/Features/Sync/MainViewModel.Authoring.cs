using CommunityToolkit.Mvvm.ComponentModel;
using Stacker.Core;

namespace Stacker.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public WorkingTreeViewModel? WorkingTreeEditor { get; }
    public bool CanOpenWorkingTree => CanEdit && WorkingTreeEditor is not null;
    [ObservableProperty] private bool _isWorkingTreeOpen;

    public async Task OpenWorkingTreeAsync()
    {
        if (!CanOpenWorkingTree || WorkingTreeEditor is null)
            return;
        IsWorkingTreeOpen = true;
        await WorkingTreeEditor.RefreshAsync();
    }

    public void CloseWorkingTree()
    {
        IsWorkingTreeOpen = false;
        WorkingTreeEditor?.Close();
    }

    internal async Task RefreshAfterIndexChangeAsync(bool committed)
    {
        if (committed)
            await ReloadLocalAsync();
        else
            await RefreshWorkingTreeAsync();
    }

    private async Task ReloadLocalAsync()
    {
        if (Repository is not { } repository)
            return;
        var applying = _runtime._applyingChanges;
        _runtime._applyingChanges = true;
        try { await ReadRepositoryAsync(repository.Root, opening: false); }
        finally { _runtime._applyingChanges = applying; }
    }
}
