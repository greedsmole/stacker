using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class HistoryWorkspaceViewModel
{
    private sealed record LayerLoad(int Generation, CancellationTokenSource Cancellation);

    private LayerLoad? _layerLoad;
    private bool _executingCommand;
    internal CancellationToken LayerLoadToken => _layerLoad?.Cancellation.Token ?? _lifetime.Token;

    private void UpdateBusy() => IsBusy = !_disposed && (_layerLoad is not null || _executingCommand);

    partial void OnSelectedNodeChanged(ChangeNode? value)
    {
        _generation++;
        var previous = _layerLoad;
        _layerLoad = null;
        previous?.Cancellation.Cancel();
        _current = null;
        // Resetting the selection must not navigate the main comparison.
        var wasLoading = _loadingSelection;
        _loadingSelection = true;
        try { SelectedFile = null; }
        finally { _loadingSelection = wasLoading; }
        PreviousVersion = null;
        Files.Clear();
        Versions.Clear();
        Preview = "";
        ClearDocument();
        Message = value is null ? "Select a layer to load its review history." : "Select Load layer to read this layer's files and versions.";
        UpdateBusy();
        NotifySelectionCommands();
    }

    [RelayCommand(CanExecute = nameof(CanLoadLayer))]
    private Task LoadLayerAsync() => LoadCurrentDiffAsync();

    internal async Task LoadCurrentDiffAsync(string? filePath = null)
    {
        if (await LoadLayerDataAsync(filePath) && CanShowCurrent) await ShowCurrentAsync();
    }

    private async Task<bool> LoadLayerDataAsync(string? filePath = null)
    {
        if (_disposed || SelectedNode is not { } node) return false;
        filePath ??= SelectedFile?.Change.NewPath;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var previous = _layerLoad;
        var load = new LayerLoad(++_generation, cancellation);
        _layerLoad = load;
        try
        {
            previous?.Cancellation.Cancel();
            UpdateBusy();
            var token = cancellation.Token;
            var versions = (await _store.VersionsAsync(node.Id, token)).OrThrow();
            var current = _path.First(p => p.Node.Id == node.Id).Version;
            var files = (await _git.FilesAsync(_root, current.MergeBase!, current.HeadSha, token)).OrThrow();
            var status = await _progress.StatusAsync(_repository, Graph.Scope, current, files, token);
            if (!OwnsLoad(load)) return false;
            var wasLoading = _loadingSelection;
            _loadingSelection = true;
            try
            {
                _current = current;
                Versions.Clear();
                foreach (var version in versions.Where(v => v.Id != current.Id).Reverse()) Versions.Add(version);
                PreviousVersion = Versions.FirstOrDefault();
                Files.Clear();
                foreach (var file in status) Files.Add(file);
                SelectedFile = Files.FirstOrDefault(file => file.Change.NewPath == filePath) ?? Files.FirstOrDefault();
            }
            finally { _loadingSelection = wasLoading; }
            if (SelectionChanged is not null) await SyncSelectionAsync(node, SelectedFile?.Change);
            if (!OwnsLoad(load)) return false;
            Message = $"{status.Count(f => f.State == FileReviewState.Reviewed)}/{status.Count} files reviewed on this layer.";
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (OwnsLoad(load)) Message = ex.Message;
            return false;
        }
        finally
        {
            if (ReferenceEquals(_layerLoad, load))
            {
                _layerLoad = null;
                UpdateBusy();
            }
        }
    }

    private bool OwnsLoad(LayerLoad load) =>
        !_disposed && load.Generation == _generation && ReferenceEquals(_layerLoad, load) && !load.Cancellation.IsCancellationRequested;
}
