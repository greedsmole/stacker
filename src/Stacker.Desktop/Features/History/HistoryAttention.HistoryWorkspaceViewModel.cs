using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class HistoryWorkspaceViewModel
{
    [ObservableProperty] private HistoryAttentionItem? _selectedAttention;
    [ObservableProperty] private string _concernText = "";
    [RelayCommand]
    private async Task OpenAttentionAsync()
    {
        if (SelectedAttention is not { } item || IsBusy)
            return;
        SelectedNode = Nodes.FirstOrDefault(n => n.Id == item.ChangeId);
        if (!await LoadLayerDataAsync(item.Path))
            return;
        SelectedFile = Files.FirstOrDefault(f => f.Change.NewPath == item.Path);
        if (SelectedFile is null)
        {
            Preview = item.Label;
            Message = "This question refers to a file no longer changed by the current layer. It remains blocking until explicitly cleared.";
        }
        else
            await ShowCurrentAsync();
    }
    [RelayCommand]
    private async Task ClearQueuedBlockerAsync()
    {
        if (SelectedAttention is not { HasConcern: true } item)
            return;
        await ExecuteAsync(async () =>
        {
            (await _store.SetConcernAsync(new(item.ChangeId, item.Path, ""), true, _lifetime.Token)).OrThrow();
            Message = "Queued blocking question cleared. Refresh Needs attention to rebuild the queue.";
        });
    }
    [RelayCommand] private async Task FlagBlockerAsync() => await SetConcernAsync(false);
    [RelayCommand] private async Task ClearBlockerAsync() => await SetConcernAsync(true);
    private async Task SetConcernAsync(bool remove)
    {
        if (_current is not { } current || SelectedFile is not { } file || IsBusy)
            return;
        if (!remove && string.IsNullOrWhiteSpace(ConcernText))
        {
            Message = "Describe the blocking question first.";
            return;
        }
        await ExecuteAsync(async () =>
        {
            (await _store.SetConcernAsync(new(current.ChangeId, file.Change.NewPath, ConcernText.Trim()), remove, _lifetime.Token)).OrThrow();
            await LoadLayerDataAsync(file.Change.NewPath);
        });
    }
}
