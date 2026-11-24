using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Desktop.ViewModels;

public sealed partial class WorkingTreeViewModel(MainViewModel owner, IWorkingTreeAuthoring authoring) : ObservableObject
{
    private WorkingTreeSnapshot? _snapshot;
    private CancellationTokenSource? _refresh;
    private CancellationTokenSource? _patch;
    private string? _documentRoot;
    private WorkingTreeSide _side;
    private WorkingTreeFile[] _selected = [];
    private bool _switchingList;

    [ObservableProperty] private IReadOnlyList<WorkingTreeFile> _files = [];
    [ObservableProperty] private WorkingTreeFile? _selectedFile;
    [ObservableProperty] private IReadOnlyList<RenderedDiffLine> _lines = [];
    [ObservableProperty] private string _commitMessage = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _patchMessage = "Select a file to inspect its changes.";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoadingPatch;
    [ObservableProperty] private bool _isWriting;

    public string UnstagedLabel => $"Unstaged ({_snapshot?.State.Entries.Count(entry => entry.IsUnstaged) ?? 0})";
    public string StagedLabel => $"Staged ({_snapshot?.State.Entries.Count(entry => entry.IsStaged) ?? 0})";
    public string Checkout => _snapshot is { } data ? data.State.Branch ?? "Detached HEAD" : "Reading checkout";
    public bool ShowUnstaged => _side == WorkingTreeSide.Unstaged;
    public bool ShowStaged => _side == WorkingTreeSide.Staged;
    public string IndexAction => ShowStaged ? "Unstage selected" : "Stage selected";
    public bool HasConflicts => _snapshot?.State.HasConflicts == true;
    public IReadOnlyList<string> Conflicts => _snapshot?.State.ConflictedFiles ?? [];
    public bool CanUpdateIndex => _selected.Length > 0 && CanEditIndex;
    public bool HasMessage => Message.Length > 0;
    public bool HasPatchMessage => PatchMessage.Length > 0;
    public bool HasLines => Lines.Count > 0;
    public string EmptyFiles => ShowStaged ? "Stage files to prepare a commit." : "No unstaged changes in this worktree.";
    public bool ShowEmptyFiles => !IsLoading && Files.Count == 0 && _snapshot is not null;
    public string PatchTitle => SelectedFile?.DisplayPath ?? "File changes";
    private bool CanEditIndex => !IsLoading && !IsWriting && !owner.IsSyncing && _snapshot is { State.NeedsAttention: false };
    public bool CanCommitStaged => CanEditIndex && _snapshot!.State.Entries.Any(entry => entry.IsStaged)
        && !string.IsNullOrWhiteSpace(CommitMessage);

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnPatchMessageChanged(string value) => OnPropertyChanged(nameof(HasPatchMessage));
    partial void OnLinesChanged(IReadOnlyList<RenderedDiffLine> value) => OnPropertyChanged(nameof(HasLines));
    partial void OnCommitMessageChanged(string value) => UpdateCommands();
    partial void OnIsWritingChanged(bool value) => UpdateCommands();
    partial void OnIsLoadingChanged(bool value)
    {
        UpdateCommands();
        OnPropertyChanged(nameof(ShowEmptyFiles));
    }

    partial void OnSelectedFileChanged(WorkingTreeFile? value)
    {
        OnPropertyChanged(nameof(PatchTitle));
        if (!_switchingList)
            _ = LoadPatchAsync(value);
    }

    public async Task RefreshAsync(string? notice = null)
    {
        if (owner.Repository is not { } repository || !owner.IsWorkingTreeOpen)
            return;
        _refresh?.Cancel();
        _patch?.Cancel();
        var refresh = new CancellationTokenSource();
        _refresh = refresh;
        if (_documentRoot != repository.Root)
        {
            _documentRoot = repository.Root;
            _snapshot = null;
            CommitMessage = "";
            _side = WorkingTreeSide.Unstaged;
            SetFiles(null);
        }
        IsLoading = true;
        Message = notice ?? "";
        try
        {
            var result = await authoring.ReadAsync(repository.Root, refresh.Token);
            if (!IsCurrent(refresh, repository.Root))
                return;
            if (result.IsFailed)
            {
                _snapshot = null;
                SetFiles(null);
                Message = result.Problem!.Message;
                return;
            }
            var previous = SelectedFile;
            _snapshot = result.Value;
            owner.WorkingTree = _snapshot!.State;
            if (owner.HasGhStackRecovery)
                await owner.RefreshGhStackRecoveryAsync(refresh.Token);
            if (!IsCurrent(refresh, repository.Root))
                return;
            if (notice is null && _snapshot!.State.NeedsAttention)
                Message = _snapshot.State.Summary;
            SetFiles(previous);
            await LoadPatchAsync(SelectedFile);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrent(refresh, repository.Root))
                Message = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_refresh, refresh))
            {
                _refresh = null;
                IsLoading = false;
            }
            refresh.Dispose();
        }
    }

    private bool IsCurrent(CancellationTokenSource refresh, string root) =>
        !refresh.IsCancellationRequested && ReferenceEquals(_refresh, refresh)
        && owner.IsWorkingTreeOpen && owner.Repository?.Root == root;

    public void SelectSide(WorkingTreeSide side)
    {
        _side = side;
        SetFiles(SelectedFile);
        _ = LoadPatchAsync(SelectedFile);
    }

    internal void SetSelection(IEnumerable<WorkingTreeFile> files)
    {
        if (_switchingList)
            return;
        _selected = files.Where(Files.Contains).ToArray();
        UpdateCommands();
    }

    private void SetFiles(WorkingTreeFile? previous)
    {
        _switchingList = true;
        var entries = _snapshot?.State.Entries ?? [];
        Files = entries.Select(entry => new WorkingTreeFile(entry, _side)).Where(file => file.IsAvailable).ToArray();
        SelectedFile = Files.FirstOrDefault(file => file.Path == previous?.Path) ?? Files.FirstOrDefault();
        _selected = SelectedFile is { } selected ? [selected] : [];
        _switchingList = false;
        OnPropertyChanged(nameof(Checkout));
        OnPropertyChanged(nameof(UnstagedLabel));
        OnPropertyChanged(nameof(StagedLabel));
        OnPropertyChanged(nameof(ShowUnstaged));
        OnPropertyChanged(nameof(ShowStaged));
        OnPropertyChanged(nameof(IndexAction));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(Conflicts));
        OnPropertyChanged(nameof(EmptyFiles));
        OnPropertyChanged(nameof(ShowEmptyFiles));
        UpdateCommands();
    }

    private async Task LoadPatchAsync(WorkingTreeFile? file)
    {
        _patch?.Cancel();
        Lines = [];
        PatchMessage = file is null ? "Select a file to inspect its changes." : "";
        if (file is null || _snapshot is not { } snapshot || !owner.IsWorkingTreeOpen)
        {
            IsLoadingPatch = false;
            return;
        }
        var load = new CancellationTokenSource();
        _patch = load;
        IsLoadingPatch = true;
        try
        {
            var result = await authoring.ReadPatchAsync(snapshot, file, load.Token);
            if (load.IsCancellationRequested || !ReferenceEquals(_patch, load) || !owner.IsWorkingTreeOpen)
                return;
            if (result.IsFailed)
            {
                PatchMessage = result.Problem!.Message;
                return;
            }
            var diff = DiffView.From(result.Value!);
            var wrapping = owner.Settings.WrapCode ? TextWrapping.Wrap : TextWrapping.NoWrap;
            Lines = diff.Rows.Select(line => new RenderedDiffLine(line) { AllowComment = false, TextWrapping = wrapping }).ToArray();
            PatchMessage = diff.EmptyReason;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!load.IsCancellationRequested && ReferenceEquals(_patch, load))
                PatchMessage = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_patch, load))
            {
                _patch = null;
                IsLoadingPatch = false;
            }
            load.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateIndex))]
    private Task ChangeIndexAsync() => MutateAsync(async snapshot =>
    {
        var staging = ShowUnstaged;
        var result = await authoring.UpdateIndexAsync(snapshot, _side, _selected);
        return (result.IsOk, result.Problem?.Message ?? (staging ? "Staged selected files." : "Unstaged selected files."));
    }, commit: false);

    [RelayCommand(CanExecute = nameof(CanCommitStaged))]
    private Task CommitStagedAsync() => MutateAsync(async snapshot =>
    {
        var result = await authoring.CommitStagedAsync(snapshot, CommitMessage);
        return (result.IsOk, result.Problem?.Message ?? $"Created commit {result.Value![..8]}.");
    }, commit: true);

    [RelayCommand]
    private async Task OpenConflictsAsync()
    {
        var result = await owner.OpenConflictsInEditorAsync();
        Message = result.Message;
    }

    [RelayCommand]
    private async Task StageResolvedAsync()
    {
        if (!HasConflicts || IsWriting || owner.IsSyncing)
            return;
        IsWriting = true;
        owner.IsSyncing = true;
        try
        {
            var result = await owner.StageConflictsAsync();
            await RefreshAsync(result.Message);
        }
        finally
        {
            owner.IsSyncing = false;
            IsWriting = false;
        }
    }

    private async Task MutateAsync(Func<WorkingTreeSnapshot, Task<(bool Ok, string Message)>> action, bool commit)
    {
        if (!CanEditIndex || _snapshot is not { } snapshot || owner.Repository?.Root != snapshot.Root)
            return;
        IsWriting = true;
        owner.IsSyncing = true;
        try
        {
            var (ok, message) = await action(snapshot);
            if (ok && commit)
                CommitMessage = "";
            await owner.RefreshAfterIndexChangeAsync(ok && commit);
            await RefreshAsync(message);
        }
        catch (Exception ex) { Message = ex.Message; }
        finally
        {
            owner.IsSyncing = false;
            IsWriting = false;
        }
    }

    internal void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanUpdateIndex));
        OnPropertyChanged(nameof(CanCommitStaged));
        ChangeIndexCommand.NotifyCanExecuteChanged();
        CommitStagedCommand.NotifyCanExecuteChanged();
    }

    public void Close()
    {
        _refresh?.Cancel();
        _patch?.Cancel();
    }
}
