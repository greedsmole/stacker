using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
namespace Stacker.Desktop.ViewModels;

public sealed partial class HistoryWorkspaceViewModel
{

    private LayerLoad? _layerLoad;
    private readonly Dictionary<string, ChangeVersion> _materialized = new(StringComparer.Ordinal);
    private bool _historyCodeRemoved;
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
        try
        {
            SelectedFile = null;
        }
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

    [RelayCommand(CanExecute = nameof(CanLoadLayer), AllowConcurrentExecutions = true)]
    private Task LoadLayerAsync() => LoadCurrentDiffAsync();

    internal Task LoadCurrentDiffAsync(string? filePath = null, bool synchronize = true) =>
        LoadLayerDataAsync(filePath, showCurrent: true, synchronize: synchronize);

    private async Task<bool> LoadLayerDataAsync(string? filePath = null, bool showCurrent = false, bool synchronize = true)
    {
        if (_disposed || SelectedNode is not { } node)
            return false;
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
            var current = await ReadVersionAsync(_path.First(p => p.Node.Id == node.Id).Version, token);
            var files = ExistingFiles?.Invoke(current) ?? (await _git.FilesAsync(_root, current.MergeBase!, current.HeadSha, token)).OrThrow();
            if (!OwnsLoad(load))
                return false;
            _current = current;
            if (showCurrent)
            {
                var path = files.FirstOrDefault(file => file.NewPath == filePath)?.NewPath ?? files.FirstOrDefault()?.NewPath;
                await ShowDocumentAsync(new(DiffDocumentMode.Current, [new(current.Label, _root, current.MergeBase!, current.HeadSha, files)],
                    "Original layer diff"), path, current.Id);
            }
            token.ThrowIfCancellationRequested();
            var versions = (await _store.VersionsAsync(node.Id, token)).OrThrow();
            var status = await _progress.StatusAsync(_repository, Graph.Scope, current, files, token);
            if (!OwnsLoad(load))
                return false;
            var wasLoading = _loadingSelection;
            _loadingSelection = true;
            try
            {
                Versions.Clear();
                foreach (var version in versions.Where(v => v.Id != current.Id).Reverse())
                    Versions.Add(version);
                PreviousVersion = Versions.FirstOrDefault();
                Files.Clear();
                foreach (var file in status)
                    Files.Add(file);
                var displayedPath = showCurrent ? SelectedDocumentFile?.File.NewPath ?? filePath : filePath;
                SelectedFile = Files.FirstOrDefault(file => file.Change.NewPath == displayedPath) ?? Files.FirstOrDefault();
            }
            finally { _loadingSelection = wasLoading; }
            if (synchronize && SelectionChanged is not null)
                await SyncSelectionAsync(node, SelectedFile?.Change);
            if (!OwnsLoad(load))
                return false;
            Message = $"{status.Count(f => f.State == FileReviewState.Reviewed)}/{status.Count} files reviewed on this layer.";
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (OwnsLoad(load))
                Message = ex.Message;
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

    private async Task<ChangeVersion> ReadVersionAsync(ChangeVersion version, CancellationToken ct)
    {
        if (_historyCodeRemoved)
            throw new StackerException("Reopen history to capture this layer again.");
        if (MaterializeVersion is null)
            return version;
        if (_materialized.TryGetValue(version.Id, out var cached))
            return cached;
        var current = await MaterializeVersion(version, ct);
        if (ContentLease is null)
        {
            var lease = (await _objects.AcquireAsync(_repository, Graph.Scope, ct)).OrThrow();
            if (ct.IsCancellationRequested || ContentLease is not null)
                lease.Dispose();
            else
                ContentLease = lease;
        }
        ct.ThrowIfCancellationRequested();
        _materialized[version.Id] = current;
        return current;
    }

    private async Task<IReadOnlyList<(ChangeNode Node, ChangeVersion Version)>> MaterializePathAsync(CancellationToken ct)
    {
        if (MaterializeVersion is null)
            return _path;
        var path = new List<(ChangeNode, ChangeVersion)>(_path.Count);
        foreach (var (node, version) in _path)
        {
            try { path.Add((node, await ReadVersionAsync(version, ct))); }
            catch (StackerException ex) { throw new StackerException(node.Title + " · " + ex.Message); }
        }
        return path;
    }
}
