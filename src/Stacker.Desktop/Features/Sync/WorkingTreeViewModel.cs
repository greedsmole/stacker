using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stacker.Core;
using Stacker.Core.Framework;

namespace Stacker.Desktop.ViewModels;

public sealed partial class WorkingTreeViewModel(MainViewModel owner, IWorkingTreeAuthoring authoring) : ObservableObject
{
    private MainViewModel Owner { get; } = owner;
    private IWorkingTreeAuthoring Authoring { get; } = authoring;
    private WorkingTreeSnapshot? _snapshot;
    private CancellationTokenSource? _refresh;
    private CancellationTokenSource? _patch;
    private string? _documentRoot;
    private WorkingTreeSide _side;
    private IReadOnlyList<WorkingTreeFile> _unstaged = [];
    private IReadOnlyList<WorkingTreeFile> _staged = [];
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

    public string UnstagedLabel => $"Unstaged ({_unstaged.Count})";
    public string StagedLabel => $"Staged ({_staged.Count})";
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
    private bool CanEditIndex => !IsLoading && !IsWriting && !Owner.IsSyncing && _snapshot is { State.NeedsAttention: false };
    public bool CanCommitStaged => CanEditIndex && _staged.Count > 0
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

    public async Task<(bool Ok, string Message)> RefreshAsync(string? notice = null, CancellationToken ct = default)
    {
        if (Owner.Repository is not { } repository || !Owner.IsWorkingTreeOpen)
            return (false, "Open the working tree first.");
        _refresh?.Cancel();
        _patch?.Cancel();
        var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _refresh = refresh;
        if (_documentRoot != repository.Root)
        {
            _documentRoot = repository.Root;
            SetSnapshot(null);
            CommitMessage = "";
            _side = WorkingTreeSide.Unstaged;
            SetFiles(null);
            Lines = [];
        }
        IsLoading = true;
        Message = notice ?? "";
        try
        {
            var result = await Authoring.ReadAsync(repository.Root, refresh.Token);
            if (!IsCurrent(refresh, repository.Root))
                return (false, "Working tree changed.");
            if (result.IsFailed)
            {
                SetSnapshot(null);
                SetFiles(null);
                _ = LoadPatchAsync(null);
                Message = result.Problem!.Message;
                return (false, Message);
            }
            var previous = SelectedFile;
            var snapshot = result.Value!;
            SetSnapshot(snapshot);
            if (notice is null && snapshot.State.NeedsAttention)
                Message = snapshot.State.Summary;
            SetFiles(previous);
            IsLoading = false;
            var summary = Owner.RefreshWorkingTreeSummaryAsync(refresh.Token, snapshot.State);
            _ = LoadPatchAsync(SelectedFile);
            return await summary;
        }
        catch (OperationCanceledException) { return (false, "Working tree refresh cancelled."); }
        catch (Exception ex)
        {
            if (IsCurrent(refresh, repository.Root))
                Message = ex.Message;
            return (false, ex.Message);
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
        && Owner.IsWorkingTreeOpen && Owner.Repository?.Root == root;

    public void SelectSide(WorkingTreeSide side)
    {
        if (_side == side)
            return;
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

    private void SetSnapshot(WorkingTreeSnapshot? snapshot)
    {
        _snapshot = snapshot;
        var entries = snapshot?.State.Entries ?? [];
        _unstaged = entries.Where(entry => entry.IsUnstaged).Select(entry => new WorkingTreeFile(entry, WorkingTreeSide.Unstaged)).ToArray();
        _staged = entries.Where(entry => entry.IsStaged).Select(entry => new WorkingTreeFile(entry, WorkingTreeSide.Staged)).ToArray();
    }

    private void SetFiles(WorkingTreeFile? previous)
    {
        _switchingList = true;
        Files = ShowStaged ? _staged : _unstaged;
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

    [RelayCommand(CanExecute = nameof(CanUpdateIndex))]
    private Task ChangeIndexAsync() => MutateAsync(async snapshot =>
    {
        var staging = ShowUnstaged;
        var result = await Authoring.UpdateIndexAsync(snapshot, _side, _selected);
        return (result.IsOk, result.Problem?.Message ?? (staging ? "Staged selected files." : "Unstaged selected files."));
    }, commit: false);

    [RelayCommand(CanExecute = nameof(CanCommitStaged))]
    private Task CommitStagedAsync() => MutateAsync(async snapshot =>
    {
        var result = await Authoring.CommitStagedAsync(snapshot, CommitMessage);
        return (result.IsOk, result.Problem?.Message ?? $"Created commit {result.Value![..8]}.");
    }, commit: true);

    [RelayCommand]
    private async Task OpenConflictsAsync()
    {
        var result = await Owner.OpenConflictsInEditorAsync();
        Message = result.Message;
    }

    [RelayCommand]
    private async Task StageResolvedAsync()
    {
        if (!HasConflicts || IsWriting || Owner.IsSyncing)
            return;
        IsWriting = true;
        Owner.IsSyncing = true;
        try
        {
            var result = await Owner.StageConflictsAsync();
            Message = result.Message;
        }
        finally
        {
            Owner.IsSyncing = false;
            IsWriting = false;
        }
    }

    private async Task MutateAsync(Func<WorkingTreeSnapshot, Task<(bool Ok, string Message)>> action, bool commit)
    {
        if (!CanEditIndex || _snapshot is not { } snapshot || Owner.Repository?.Root != snapshot.Root)
            return;
        _patch?.Cancel();
        IsWriting = true;
        Owner.IsSyncing = true;
        try
        {
            var (ok, message) = await action(snapshot);
            if (ok && commit)
                CommitMessage = "";
            await Owner.RefreshAfterIndexChangeAsync(ok && commit, message);
        }
        catch (Exception ex) { Message = ex.Message; }
        finally
        {
            Owner.IsSyncing = false;
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
